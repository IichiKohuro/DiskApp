using System.Threading.Channels;

using DiskApp.Core.Abstractions;
using DiskApp.Core.Domain;
using DiskApp.Core.Localization;

namespace DiskApp.Core.Indexing.Watching;

/// <summary>
/// Цикл обработки событий одного корня: события наблюдателя попадают в канал, склеиваются <see cref="ChangeCoalescer"/>
/// и после паузы применяются <see cref="ChangeApplier"/>. Переполнение буфера ОС и сбой применения превращаются в запрос
/// сверки корня, сбой наблюдателя — в запрос проверки доступности. Пока включена пауза, изменения копятся.
/// </summary>
public sealed class RootChangeProcessor : IAsyncDisposable
{
    private readonly Root _root;
    private readonly IRootWatcher _watcher;
    private readonly ChangeApplier _applier;
    private readonly Func<ExclusionMatcher> _matcher;
    private readonly RootIndexState _state;
    private readonly ChangeCoalescer _coalescer;
    private readonly IClock _clock;
    private readonly PauseToken _pause;
    private readonly Action<RootRequest> _onRequest;
    private readonly Channel<RawChange> _channel = Channel.CreateUnbounded<RawChange>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private int _overflowSignal;

    /// <summary>
    /// Создаёт обработчик; наблюдатель запускается в <see cref="Start"/>.
    /// </summary>
    /// <param name="root">Корень.</param>
    /// <param name="watcher">Наблюдатель ОС.</param>
    /// <param name="applier">Применение изменений к индексу.</param>
    /// <param name="matcher">Текущие правила исключений (могут меняться в настройках).</param>
    /// <param name="state">Состояние корня.</param>
    /// <param name="coalescer">Склейка событий.</param>
    /// <param name="clock">Часы.</param>
    /// <param name="pause">Пауза индексации.</param>
    /// <param name="onRequest">Запросы оркестратору: сверить корень или поддерево, проверить доступность.</param>
    public RootChangeProcessor(
        Root root,
        IRootWatcher watcher,
        ChangeApplier applier,
        Func<ExclusionMatcher> matcher,
        RootIndexState state,
        ChangeCoalescer coalescer,
        IClock clock,
        PauseToken pause,
        Action<RootRequest> onRequest)
    {
        _root = root;
        _watcher = watcher;
        _applier = applier;
        _matcher = matcher;
        _state = state;
        _coalescer = coalescer;
        _clock = clock;
        _pause = pause;
        _onRequest = onRequest;
    }

    /// <summary>Сколько пакетов применено; для тестов и диагностики.</summary>
    public int AppliedBatches { get; private set; }

    /// <summary>
    /// Подписывается на наблюдателя, запускает его и цикл обработки.
    /// </summary>
    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _watcher.Changed += OnChanged;
        _watcher.Failed += OnFailed;
        _watcher.Start();
        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    /// <summary>
    /// Ставит пути на перепроверку, как если бы о них пришли события (после полного сканирования).
    /// </summary>
    public void Recheck(IEnumerable<string> relPaths)
    {
        ArgumentNullException.ThrowIfNull(relPaths);

        foreach (var relPath in relPaths)
        {
            _channel.Writer.TryWrite(new RawChange(RawChangeKind.Changed, relPath));
        }
    }

    /// <summary>
    /// Останавливает наблюдателя и цикл; накопленные изменения не применяются (их догонит сверка при следующем запуске).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _watcher.Changed -= OnChanged;
        _watcher.Failed -= OnFailed;
        _watcher.Dispose();
        _channel.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Остановка.
            }
        }

        _stop.Dispose();
    }

    private void OnChanged(object? sender, RawChange change) => _channel.Writer.TryWrite(change);

    private void OnFailed(object? sender, WatcherFailure failure)
    {
        if (failure.IsOverflow)
        {
            Interlocked.Exchange(ref _overflowSignal, 1);
            _channel.Writer.TryWrite(new RawChange(RawChangeKind.Changed, string.Empty));
        }
        else
        {
            _onRequest(new RootRequest(_root.Id, RootRequestKind.CheckAvailability, string.Empty, failure.Message));
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var reader = _channel.Reader;
        while (!cancellationToken.IsCancellationRequested)
        {
            var due = _coalescer.DueAt;
            if (_pause.IsPaused)
            {
                // Ждём либо событий (копить их), либо снятия паузы (применить накопленное).
                var waitForEvents = reader.WaitToReadAsync(cancellationToken).AsTask();
                var completed = await Task.WhenAny(waitForEvents, _pause.WaitWhilePausedAsync(cancellationToken)).ConfigureAwait(false);
                if (completed == waitForEvents && !await waitForEvents.ConfigureAwait(false))
                {
                    return;
                }
            }
            else if (due is null)
            {
                if (!await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            else
            {
                var delay = due.Value - _clock.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    await Task.WhenAny(reader.WaitToReadAsync(cancellationToken).AsTask(), Task.Delay(delay, cancellationToken)).ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            while (reader.TryRead(out var change))
            {
                _coalescer.Add(change, _clock.UtcNow);
            }

            if (Interlocked.Exchange(ref _overflowSignal, 0) == 1)
            {
                _coalescer.MarkOverflowed();
            }

            if (!_pause.IsPaused && _coalescer.IsDue(_clock.UtcNow))
            {
                await FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task FlushAsync(CancellationToken cancellationToken)
    {
        var batch = _coalescer.Drain();
        if (batch.Overflowed)
        {
            _onRequest(new RootRequest(_root.Id, RootRequestKind.Reconcile, string.Empty, L.T("RootChangeProcessor.FileSystemEventBufferOverflow")));
            return;
        }

        if (batch.IsEmpty)
        {
            return;
        }

        try
        {
            var result = await _applier.ApplyAsync(_root, batch, _matcher(), _state, cancellationToken).ConfigureAwait(false);
            AppliedBatches++;
            foreach (var directory in result.NewDirectories)
            {
                // Появление самой папки уже в истории файлов; её содержимое — часть того же события.
                _onRequest(new RootRequest(_root.Id, RootRequestKind.Reconcile, directory, L.T("RootChangeProcessor.NewFolderReconcilingItsContents"), Journal: false));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _onRequest(new RootRequest(_root.Id, RootRequestKind.Reconcile, string.Empty, L.T("RootChangeProcessor.ErrorApplyingChanges") + exception.Message));
        }
    }
}

/// <summary>
/// Запрос обработчика событий к оркестратору.
/// </summary>
/// <param name="RootId">Корень.</param>
/// <param name="Kind">Что нужно.</param>
/// <param name="RelPath">Поддерево для сверки; пустая строка — весь корень.</param>
/// <param name="Reason">Причина для журнала.</param>
/// <param name="Journal">Сверка пишет изменения в историю файлов (этап 20).</param>
public sealed record RootRequest(long RootId, RootRequestKind Kind, string RelPath, string Reason, bool Journal = true);

/// <summary>
/// Вид запроса к оркестратору.
/// </summary>
public enum RootRequestKind
{
    /// <summary>Сверить корень или поддерево.</summary>
    Reconcile,

    /// <summary>Проверить, доступен ли корень (наблюдатель упал).</summary>
    CheckAvailability,
}

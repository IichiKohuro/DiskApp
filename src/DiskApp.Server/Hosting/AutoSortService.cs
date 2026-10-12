using System.IO.Abstractions;

using DiskApp.Contracts.Text;
using DiskApp.Core.Abstractions;
using DiskApp.Core.Domain;
using DiskApp.Core.Events;
using DiskApp.Core.Operations;
using DiskApp.Core.Paths;
using DiskApp.Core.Repositories;
using DiskApp.Core.Settings;
using DiskApp.Core.Sorting;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskApp.Server.Hosting;

/// <summary>
/// Авторазбор по шаблонам рабочих папок (PLAN.md, этап 17.5, ADR-0012). Новый файл накопителя, который пришёл (прошло
/// «время успокоения») и подходит к шаблону «переносить самим» ровно одной папки, переносится обычной операцией очереди
/// с пометкой «авторазбор»: имя — по шаблону имени разбора, конфликт — «оставить оба», теги — прежние, авто-теги папка
/// даёт сама. Отложенные, «оставленные здесь», папки и файлы, подходящие к разным папкам, не трогаются. Файл, вернувшийся
/// после автопереноса (отменой или вручную), получает состояние «вручную» и больше сам не переносится. Неудачный перенос
/// (файл занят) повторяется через пару минут или когда файл изменится.
/// </summary>
internal sealed partial class AutoSortService : IHostedService, IDisposable
{
    /// <summary>Сколько помнить перенесённых файлов, чтобы узнать вернувшиеся.</summary>
    private const int MaxRemembered = 2_000;

    private readonly InboxService _inbox;
    private readonly SortingService _sorting;
    private readonly FileOperationService _operations;
    private readonly ISettingsStore<AppSettings> _settings;
    private readonly IInboxStateRepository _states;
    private readonly IRootRepository _roots;
    private readonly IIndexEventBus _events;
    private readonly IClock _clock;
    private readonly PathNormalizer _paths;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, InboxItem[]> _inFlight = [];
    private readonly HashSet<string> _inFlightPaths;
    private readonly Dictionary<string, Remembered> _moved;
    private readonly Dictionary<string, Remembered> _failed;
    private readonly HashSet<string> _missingDestinations;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Timer _timer;
    private (long RootId, string Relative)[] _watched = [];
    private DateTimeOffset? _due;
    private bool _busy;
    private bool _rerun;
    private bool _stopped;
    private long _order;
    private Task _running = Task.CompletedTask;

    /// <summary>
    /// Создаёт службу; события слушаются с <see cref="StartAsync"/>.
    /// </summary>
    public AutoSortService(
        InboxService inbox,
        SortingService sorting,
        FileOperationService operations,
        ISettingsStore<AppSettings> settings,
        IInboxStateRepository states,
        IRootRepository roots,
        IIndexEventBus events,
        IClock clock,
        PathNormalizer paths,
        IFileSystem fileSystem,
        ILogger<AutoSortService>? logger = null)
    {
        _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
        _sorting = sorting ?? throw new ArgumentNullException(nameof(sorting));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _states = states ?? throw new ArgumentNullException(nameof(states));
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _logger = logger ?? NullLogger<AutoSortService>.Instance;
        _inFlightPaths = new HashSet<string>(paths.Comparer);
        _moved = new Dictionary<string, Remembered>(paths.Comparer);
        _failed = new Dictionary<string, Remembered>(paths.Comparer);
        _missingDestinations = new HashSet<string>(paths.Comparer);
        _timer = new Timer(_ => OnTimer());
    }

    /// <summary>Пауза после события, чтобы пачка изменений дала один проход.</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Через сколько повторить перенос, который не удался (файл занят).</summary>
    public TimeSpan RetryAfter { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Пауза после запуска сервера: индекс успевает прочитать корни.</summary>
    public TimeSpan StartDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Новое имя по шаблону имени разбора (<c>sorting.nameTemplate</c>); <c>null</c> — шаблона нет или он ничего не меняет.
    /// Дата и время — местные, как при ручном переносе.
    /// </summary>
    public static string? NewName(string? template, InboxItem item, int number, int count, string destination, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (string.IsNullOrWhiteSpace(template))
        {
            return null;
        }

        var folder = Path.GetFileName(destination.TrimEnd('\\', '/'));
        var name = FileNameTemplate.Apply(template.Trim(), new FileNameContext(item.Entry.Name, item.Entry.IsDirectory, number, count, [], folder, now.ToLocalTime()));
        return string.Equals(name, item.Entry.Name, StringComparison.Ordinal) ? null : name;
    }

    /// <summary>
    /// Файл вернулся после автопереноса (отменой или вручную) — сам он больше не уйдёт, даже если состояние «вручную» ещё не
    /// записано: очередь читается сразу после возврата, а проход авторазбора — после паузы.
    /// </summary>
    public bool IsReturned(InboxItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            return _moved.TryGetValue(item.FullPath, out var moved) && moved.IsSame(item.Entry);
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscriptions.Add(_events.Subscribe<EntriesChangedEvent>(OnEntriesChanged));
        _subscriptions.Add(_events.Subscribe<RootStatusChangedEvent>(changed =>
        {
            if (IsWatched(changed.RootId))
            {
                Schedule(Debounce);
            }
        }));
        _subscriptions.Add(_events.Subscribe<OperationCompletedEvent>(completed => _ = CompleteAsync(completed.OperationId)));
        _subscriptions.Add(_settings.Subscribe(_ => Schedule(Debounce)));
        Schedule(StartDelay);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task running;
        lock (_gate)
        {
            _stopped = true;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            running = _running;
        }

        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
        await running.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _timer.Dispose();
    }

    private bool IsWatched(long rootId)
    {
        lock (_gate)
        {
            return Array.Exists(_watched, watched => watched.RootId == rootId);
        }
    }

    // Изменилось что-то внутри накопителя (скачалось, переименовано, вернулось): нужен проход.
    private void OnEntriesChanged(EntriesChangedEvent changed)
    {
        (long RootId, string Relative)[] watched;
        lock (_gate)
        {
            watched = _watched;
        }

        foreach (var (rootId, relative) in watched)
        {
            if (rootId == changed.RootId && changed.Changes.Any(change => relative.Length == 0 || IsUnder(change.RelPath, relative) || (change.IsDirectory && IsUnder(relative, change.RelPath))))
            {
                Schedule(Debounce);
                return;
            }
        }
    }

    private bool IsUnder(string relativePath, string folder)
    {
        var path = _paths.NormalizeRelative(relativePath);
        return string.Equals(path, folder, _paths.Comparison) || path.StartsWith(folder + _paths.Separator, _paths.Comparison);
    }

    private void Schedule(TimeSpan delay)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            var due = _clock.UtcNow + delay;
            if (_due is { } current && current <= due)
            {
                return;
            }

            _due = due;
            _timer.Change(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnTimer()
    {
        lock (_gate)
        {
            _due = null;
            if (_stopped)
            {
                return;
            }

            if (_busy)
            {
                _rerun = true;
                return;
            }

            _busy = true;
            _running = Task.Run(LoopAsync);
        }
    }

    // Проходы идут по одному: просьба о проходе во время прохода даёт ещё один, когда текущий закончится.
    private async Task LoopAsync()
    {
        while (true)
        {
            try
            {
                await RunOnceAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogRunFailed(_logger, exception);
            }

            lock (_gate)
            {
                if (!_rerun || _stopped)
                {
                    _busy = false;
                    return;
                }

                _rerun = false;
            }
        }
    }

    private async Task RunOnceAsync()
    {
        var sorting = _settings.Current.Sorting;
        var rules = new SortingRuleSet(sorting.WorkFolders, _paths);
        if (!rules.HasAuto)
        {
            lock (_gate)
            {
                _watched = [];
            }

            return;
        }

        var snapshot = await _inbox.GetAsync().ConfigureAwait(false);
        await WatchAsync(snapshot).ConfigureAwait(false);

        var batches = new Dictionary<(string Inbox, string Destination), List<InboxItem>>();
        var returned = new List<string>();
        DateTimeOffset? next = null;
        foreach (var item in snapshot.Items)
        {
            var verdict = rules.Match(item);
            if (verdict.Decision != SortingDecision.Auto || item.State != InboxItemState.New)
            {
                continue;
            }

            if (!snapshot.IsReady(item))
            {
                next = next is { } earlier && earlier <= item.ReadyUtc ? earlier : item.ReadyUtc;
                continue;
            }

            lock (_gate)
            {
                if (_inFlightPaths.Contains(item.FullPath))
                {
                    continue;
                }

                // Запись о переносе остаётся: по ней очередь узнаёт вернувшийся файл, пока «вручную» ещё пишется в базу.
                if (_moved.TryGetValue(item.FullPath, out var moved) && moved.IsSame(item.Entry))
                {
                    returned.Add(item.FullPath);
                    continue;
                }

                if (_failed.TryGetValue(item.FullPath, out var failed) && failed.IsSame(item.Entry) && failed.RetryUtc > snapshot.NowUtc)
                {
                    next = next is { } earlier && earlier <= failed.RetryUtc ? earlier : failed.RetryUtc;
                    continue;
                }
            }

            // Индекс ещё не знает, что файл уже перенесён, — на диске его нет.
            if (!_fileSystem.File.Exists(item.FullPath))
            {
                continue;
            }

            var destination = verdict.Matches[0].Destination;
            if (!_fileSystem.Directory.Exists(destination))
            {
                bool first;
                lock (_gate)
                {
                    first = _missingDestinations.Add(destination);
                }

                if (first)
                {
                    LogDestinationMissing(_logger, destination);
                }

                continue;
            }

            var key = (item.Inbox, destination);
            if (!batches.TryGetValue(key, out var batch))
            {
                batches[key] = batch = [];
            }

            batch.Add(item);
        }

        if (returned.Count > 0)
        {
            await _states.SetAsync(returned, InboxItemState.Manual, snapshot.NowUtc).ConfigureAwait(false);
            LogReturned(_logger, returned.Count);
        }

        foreach (var ((_, destination), items) in batches)
        {
            await EnqueueAsync(destination, items, sorting.NameTemplate, snapshot.NowUtc).ConfigureAwait(false);
        }

        if (next is { } due)
        {
            Schedule(due - _clock.UtcNow + TimeSpan.FromMilliseconds(200));
        }
    }

    // Корни и папки накопителей — чтобы на события индекса откликаться только изменениями внутри них.
    private async Task WatchAsync(InboxSnapshot snapshot)
    {
        var roots = (await _roots.GetAllAsync().ConfigureAwait(false)).ToDictionary(static root => root.Id);
        var watched = new List<(long RootId, string Relative)>();
        foreach (var folder in snapshot.Folders)
        {
            if (folder.RootId is { } rootId && roots.TryGetValue(rootId, out var root)
                && _paths.TryNormalize(folder.Path, out var normalized) && _paths.TryGetRelativePath(root.Path, normalized, out var relative))
            {
                watched.Add((rootId, relative));
            }
        }

        lock (_gate)
        {
            _watched = [.. watched];
        }
    }

    private async Task EnqueueAsync(string destination, List<InboxItem> items, string? nameTemplate, DateTimeOffset now)
    {
        var names = items.Select((item, index) => NewName(nameTemplate, item, index + 1, items.Count, destination, now)).ToArray();
        var request = new SortRequest([.. items.Select(static item => item.FullPath)], destination, NewFolder: null, Tags: [], names.Any(static name => name is not null) ? names : null, OperationOrigin.AutoSort);
        var result = await _sorting.MoveAsync(request).ConfigureAwait(false);
        if (!result.TryGetValue(out var info))
        {
            LogEnqueueFailed(_logger, destination, result.Error?.Message);
            Remember(_failed, items, now + RetryAfter);
            return;
        }

        lock (_gate)
        {
            _inFlight[info.Id] = [.. items];
            _inFlightPaths.UnionWith(items.Select(static item => item.FullPath));
        }

        // Операция могла закончиться раньше, чем её запомнили.
        if (_operations.Get(info.Id) is { IsActive: false })
        {
            await CompleteAsync(info.Id).ConfigureAwait(false);
        }
    }

    // Итог переноса: перенесённые запоминаются (вернувшийся узнаётся по пути, времени изменения и размеру), не перенесённые
    // ждут повтора, а отменённые пользователем больше сами не переносятся.
    private async Task CompleteAsync(Guid operationId)
    {
        InboxItem[]? items;
        lock (_gate)
        {
            if (!_inFlight.Remove(operationId, out items))
            {
                return;
            }

            _inFlightPaths.ExceptWith(items.Select(static item => item.FullPath));
        }

        var info = _operations.Get(operationId);
        var moved = new HashSet<string>(info?.Moved.Select(static move => move.From) ?? [], _paths.Comparer);
        var done = items.Where(item => moved.Contains(item.FullPath)).ToArray();
        var left = items.Where(item => !moved.Contains(item.FullPath)).ToArray();
        Remember(_moved, done, retryUtc: default);
        if (done.Length > 0)
        {
            LogMoved(_logger, done.Length, info!.Destination);
        }

        if (left.Length > 0)
        {
            if (info?.State == OperationState.Cancelled)
            {
                await _states.SetAsync([.. left.Select(static item => item.FullPath)], InboxItemState.Manual, _clock.UtcNow).ConfigureAwait(false);
            }
            else
            {
                Remember(_failed, left, _clock.UtcNow + RetryAfter);
                LogMoveFailed(_logger, left.Length, info?.Message);
            }
        }

        Schedule(Debounce);
    }

    private void Remember(Dictionary<string, Remembered> target, IEnumerable<InboxItem> items, DateTimeOffset retryUtc)
    {
        lock (_gate)
        {
            foreach (var item in items)
            {
                target[item.FullPath] = new Remembered(item.Entry.ModifiedUtc, item.Entry.Size, retryUtc, ++_order);
                if (ReferenceEquals(target, _moved))
                {
                    _failed.Remove(item.FullPath);
                }
            }

            if (target.Count > MaxRemembered)
            {
                foreach (var stale in target.OrderBy(static pair => pair.Value.Order).Take(target.Count - MaxRemembered).Select(static pair => pair.Key).ToArray())
                {
                    target.Remove(stale);
                }
            }
        }
    }

    [LoggerMessage(EventId = 7101, Level = LogLevel.Information, Message = "Авторазбор: {Count} → {Destination}")]
    private static partial void LogMoved(ILogger logger, int count, string? destination);

    [LoggerMessage(EventId = 7102, Level = LogLevel.Warning, Message = "Авторазбор: не перенесено {Count}: {Message}")]
    private static partial void LogMoveFailed(ILogger logger, int count, string? message);

    [LoggerMessage(EventId = 7103, Level = LogLevel.Warning, Message = "Авторазбор: перенос в {Destination} не поставлен: {Message}")]
    private static partial void LogEnqueueFailed(ILogger logger, string destination, string? message);

    [LoggerMessage(EventId = 7104, Level = LogLevel.Warning, Message = "Авторазбор: папки назначения {Destination} нет — файлы ждут ручного разбора")]
    private static partial void LogDestinationMissing(ILogger logger, string destination);

    [LoggerMessage(EventId = 7105, Level = LogLevel.Information, Message = "Авторазбор: вернувшиеся файлы ({Count}) оставлены для ручного разбора")]
    private static partial void LogReturned(ILogger logger, int count);

    [LoggerMessage(EventId = 7106, Level = LogLevel.Warning, Message = "Авторазбор: проход не удался")]
    private static partial void LogRunFailed(ILogger logger, Exception exception);

    // Файл, каким его видел авторазбор: тот же путь, время изменения и размер — тот же файл.
    private sealed record Remembered(DateTimeOffset ModifiedUtc, long Size, DateTimeOffset RetryUtc, long Order)
    {
        public bool IsSame(FileEntry entry) => entry.ModifiedUtc == ModifiedUtc && entry.Size == Size;
    }
}

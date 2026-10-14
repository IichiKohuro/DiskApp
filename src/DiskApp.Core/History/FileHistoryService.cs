using System.IO.Abstractions;

using DiskApp.Core.Abstractions;
using DiskApp.Core.Events;
using DiskApp.Core.Localization;
using DiskApp.Core.Operations;
using DiskApp.Core.Paths;
using DiskApp.Core.Results;
using DiskApp.Core.Settings;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskApp.Core.History;

/// <summary>Чем кончилось «Вернуть на место» или «Восстановить».</summary>
/// <param name="Operation">Поставленная операция возврата (переименование, перенос).</param>
/// <param name="Restored">Событие восстановления из корзины.</param>
public sealed record FileHistoryRevert(FileOperationInfo? Operation, FileEvent? Restored);

/// <summary>
/// История файлов (PLAN.md, этап 20; решение 25, ADR-0016): постоянный журнал структурных событий — создан, переименован,
/// перенесён, удалён в корзину или навсегда, восстановлен, изменены теги — с источником. Операции DiskApp пишутся по их
/// итогу (с «разбором» и «авторазбором»); всё остальное — из журнала индекса: наблюдатель (Проводник и другие программы) и
/// сверка (сделанное, пока DiskApp не работал). Изменения индекса копятся коротким окном: «исчез здесь + появился там» с
/// тем же размером и временем изменения — это перенос; изменения путей, которые трогает или только что тронула операция
/// DiskApp, отбрасываются — их уже описала она сама; появившееся и тут же исчезнувшее (временные файлы) не пишется.
/// Изменения содержимого не пишутся вовсе. Хранение — срок и предел размера из настроек, очистка раз в час.
/// </summary>
public sealed class FileHistoryService : IAsyncDisposable
{
    /// <summary>Код ошибки: события нет.</summary>
    public const string NotFoundCode = "history.not_found";

    /// <summary>Код ошибки: вернуть нельзя.</summary>
    public const string CannotRevertCode = "history.cannot_revert";

    // Переименование загрузки браузера из временного имени в настоящее — для истории это появление файла.
    private static readonly string[] TemporarySuffixes = [".crdownload", ".part", ".partial", ".download", ".tmp"];

    // Сколько шагов назад проходить по переносам, собирая историю файла.
    private const int MaxChainSteps = 20;

    private static readonly Action<ILogger, Exception?> LogFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(1, nameof(LogFailed)), "История файлов: события не записаны");

    private static readonly Action<ILogger, int, Exception?> LogCleaned =
        LoggerMessage.Define<int>(LogLevel.Information, new EventId(2, nameof(LogCleaned)), "История файлов: удалено старых событий {Count}");

    private readonly IFileHistoryRepository _repository;
    private readonly IIndexEventBus _events;
    private readonly FileOperationService _operations;
    private readonly ISettingsStore<AppSettings> _settings;
    private readonly IClock _clock;
    private readonly PathNormalizer _paths;
    private readonly IFileSystem _fileSystem;
    private readonly IRecycleBin? _recycleBin;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly List<Pending> _pending = [];
    private readonly List<IndexJournalEvent> _summaries = [];
    private readonly Queue<Guid> _completed = new();
    private readonly List<(string Path, DateTimeOffset TimeUtc)> _restored = [];
    private readonly SemaphoreSlim _processing = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private IDisposable? _journalSubscription;
    private IDisposable? _completedSubscription;
    private Task _loop = Task.CompletedTask;
    private DateTimeOffset _nextCleanup;
    private int _disposed;

    /// <summary>Создаёт службу; подписка и фоновая запись — после <see cref="Start"/>.</summary>
    public FileHistoryService(
        IFileHistoryRepository repository,
        IIndexEventBus events,
        FileOperationService operations,
        ISettingsStore<AppSettings> settings,
        IClock clock,
        PathNormalizer paths,
        IFileSystem fileSystem,
        IRecycleBin? recycleBin = null,
        ILogger<FileHistoryService>? logger = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _recycleBin = recycleBin;
        _logger = logger ?? NullLogger<FileHistoryService>.Instance;
    }

    /// <summary>Сколько ждать пару «исчез здесь + появился там», прежде чем записать изменение как есть.</summary>
    public TimeSpan MatchWindow { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Сколько после завершения операции DiskApp изменения её путей считаются её же (наблюдатель запаздывает).</summary>
    public TimeSpan OperationGrace { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Как часто записывать накопленное.</summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Как часто убирать старые события.</summary>
    public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Подписывается на события индекса и операций и запускает фоновую запись.</summary>
    public void Start()
    {
        _journalSubscription = _events.Subscribe<IndexJournalEvent>(OnJournal);
        _completedSubscription = _events.Subscribe<OperationCompletedEvent>(OnCompleted);
        _nextCleanup = _clock.UtcNow + TimeSpan.FromMinutes(1);
        _loop = Task.Run(() => LoopAsync(_stop.Token));
    }

    /// <summary>Записывает всё накопленное сразу, не дожидаясь окна сопоставления (тесты, остановка).</summary>
    public Task FlushAsync(CancellationToken cancellationToken = default) => ProcessAsync(force: true, cancellationToken);

    /// <summary>События по отбору, новые сверху.</summary>
    public Task<IReadOnlyList<FileEvent>> QueryAsync(FileHistoryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var folder = query.Folder is { } path && _paths.TryNormalize(path, out var normalized) ? normalized : query.Folder;
        return _repository.QueryAsync(query with { Folder = folder, Limit = Math.Clamp(query.Limit, 1, FileHistoryQuery.MaxLimit) }, cancellationToken);
    }

    /// <summary>
    /// История одного файла или папки, новые сверху: события по этому пути и, через переносы, по прежним путям.
    /// </summary>
    public async Task<IReadOnlyList<FileEvent>> GetFileHistoryAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!_paths.TryNormalize(path, out var current))
        {
            return [];
        }

        var found = new Dictionary<long, FileEvent>();
        var visited = new HashSet<string>(_paths.Comparer);
        for (var step = 0; step < MaxChainSteps && visited.Add(current); step++)
        {
            var events = await _repository.GetByPathAsync(current, FileHistoryQuery.MaxLimit, cancellationToken).ConfigureAwait(false);
            foreach (var item in events)
            {
                found.TryAdd(item.Id, item);
            }

            // Самый ранний переезд сюда — откуда файл пришёл.
            var arrival = events
                .Where(item => item.OldPath is not null && item.Kind is FileEventKind.Moved or FileEventKind.Renamed && _paths.Comparer.Equals(item.Path, current))
                .MinBy(static item => item.Id);
            if (arrival is null)
            {
                break;
            }

            current = arrival.OldPath!;
        }

        return [.. found.Values.OrderByDescending(static item => item.Id)];
    }

    /// <summary>Событие по номеру.</summary>
    public Task<FileEvent?> GetAsync(long id, CancellationToken cancellationToken = default) => _repository.GetAsync(id, cancellationToken);

    /// <summary>Записывает изменение своих тегов файла или папки (диалог тегов).</summary>
    public async Task RecordTagsChangedAsync(string path, bool isDirectory, IReadOnlyList<string> added, IReadOnlyList<string> removed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(added);
        ArgumentNullException.ThrowIfNull(removed);
        if (added.Count + removed.Count == 0)
        {
            return;
        }

        var details = string.Join(", ", added.Select(static tag => "+" + tag).Concat(removed.Select(static tag => "−" + tag)));
        await SaveAsync([new FileEvent(0, _clock.UtcNow, FileEventKind.TagsChanged, FileEventSource.DiskApp, path, null, isDirectory, 0, Details: details)], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Записывает сводку: корень убран из индекса — его записи ушли все разом.</summary>
    public Task RecordRootRemovedAsync(string rootPath, long count, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        return SaveAsync([new FileEvent(0, _clock.UtcNow, FileEventKind.IndexSummary, FileEventSource.DiskApp, rootPath, null, true, count, Details: L.T("History.RootRemoved"))], cancellationToken);
    }

    /// <summary>
    /// «Вернуть на место» для переименования и переноса — обратной операцией DiskApp, если файл там, где его оставило
    /// событие, а прежнее место свободно; «Восстановить» для удалённого в корзину — из корзины на прежнее место.
    /// </summary>
    public async Task<Result<FileHistoryRevert>> RevertAsync(long id, CancellationToken cancellationToken = default)
    {
        if (await _repository.GetAsync(id, cancellationToken).ConfigureAwait(false) is not { } item)
        {
            return Result.Fail<FileHistoryRevert>(new DomainError(NotFoundCode, L.T("History.EventNotFound")));
        }

        switch (item.Kind)
        {
            case FileEventKind.Moved or FileEventKind.Renamed when item.OldPath is { } oldPath:
                if (!Exists(item.Path))
                {
                    return CannotRevert(L.F("History.NotThereAnymore", item.Path));
                }

                if (Exists(oldPath))
                {
                    return CannotRevert(L.F("History.PlaceTaken", oldPath));
                }

                var oldParent = _paths.GetParent(oldPath);
                if (oldParent is null || !_fileSystem.Directory.Exists(oldParent))
                {
                    return CannotRevert(L.F("History.FolderGone", oldParent ?? oldPath));
                }

                var name = _paths.GetFileName(oldPath);
                var request = _paths.Comparer.Equals(_paths.GetParent(item.Path), oldParent)
                    ? new FileOperationRequest(FileOperationKind.Rename, [item.Path], NewName: name)
                    : new FileOperationRequest(FileOperationKind.Move, [item.Path], oldParent, TargetNames: [name]);
                var queued = _operations.Enqueue(request);
                return queued.TryGetValue(out var operation)
                    ? Result.Ok(new FileHistoryRevert(operation, null))
                    : Result.Fail<FileHistoryRevert>(queued.Error!);

            case FileEventKind.Recycled:
                // Путь в корзине из события мог устареть (корзину чистили, файл восстанавливали) — тогда ищем заново.
                var storedPath = item.Details is { Length: > 0 } details && Exists(details) ? details : _recycleBin?.Find(item.Path)?.StoredPath;
                if (_recycleBin is null || storedPath is null)
                {
                    return CannotRevert(L.T("History.NotInRecycleBin"));
                }

                if (Exists(item.Path))
                {
                    return CannotRevert(L.F("History.PlaceTaken", item.Path));
                }

                var restored = _recycleBin.Restore(storedPath, item.Path);
                if (restored.IsFailure)
                {
                    return Result.Fail<FileHistoryRevert>(restored.Error!);
                }

                lock (_gate)
                {
                    // Наблюдатель увидит восстановленный файл как появившийся — это уже записано как «Восстановлен».
                    _restored.Add((item.Path, _clock.UtcNow));
                }

                var saved = await SaveAsync([new FileEvent(0, _clock.UtcNow, FileEventKind.Restored, FileEventSource.DiskApp, item.Path, null, item.IsDirectory, item.Size)], cancellationToken).ConfigureAwait(false);
                return Result.Ok(new FileHistoryRevert(null, saved.Count > 0 ? saved[0] : null));

            default:
                return CannotRevert(L.T("History.CannotRevertKind"));
        }

        static Result<FileHistoryRevert> CannotRevert(string message) => Result.Fail<FileHistoryRevert>(new DomainError(CannotRevertCode, message));
    }

    /// <summary>Убирает события старше срока хранения и самые старые сверх предела размера.</summary>
    /// <returns>Сколько событий удалено.</returns>
    public async Task<int> CleanupAsync(CancellationToken cancellationToken = default)
    {
        var history = _settings.Current.History;
        var days = Math.Clamp(history.RetentionDays, 1, HistorySettings.MaxRetentionDays);
        var limit = (long)Math.Clamp(history.MaxSizeMegabytes, 1, HistorySettings.MaxSizeLimitMegabytes) * 1024 * 1024;
        var removed = await _repository.DeleteOlderThanAsync(_clock.UtcNow - TimeSpan.FromDays(days), cancellationToken).ConfigureAwait(false);

        // Предел размера: убираем самые старые порциями, пока история не уложится.
        for (var round = 0; round < 1_000 && await _repository.EstimateSizeAsync(cancellationToken).ConfigureAwait(false) > limit; round++)
        {
            var deleted = await _repository.DeleteOldestAsync(1_000, cancellationToken).ConfigureAwait(false);
            removed += deleted;
            if (deleted == 0)
            {
                break;
            }
        }

        if (removed > 0)
        {
            LogCleaned(_logger, removed, null);
        }

        return removed;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Останавливает и хост, и контейнер: второй раз ничего не делает.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _journalSubscription?.Dispose();
        _completedSubscription?.Dispose();
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Остановка.
        }

        try
        {
            // Накопленное за последние секунды не теряется при выходе.
            await ProcessAsync(force: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFailed(_logger, exception);
        }

        _stop.Dispose();
        _processing.Dispose();
    }

    private void OnJournal(IndexJournalEvent journal)
    {
        var now = _clock.UtcNow;
        lock (_gate)
        {
            if (journal.Source == JournalSource.Scan)
            {
                _summaries.Add(journal);
                return;
            }

            foreach (var change in journal.Changes)
            {
                _pending.Add(new Pending(change, journal.Source, now));
            }
        }
    }

    private void OnCompleted(OperationCompletedEvent completed)
    {
        lock (_gate)
        {
            _completed.Enqueue(completed.OperationId);
        }
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await ProcessAsync(force: false, cancellationToken).ConfigureAwait(false);
                if (_clock.UtcNow >= _nextCleanup)
                {
                    _nextCleanup = _clock.UtcNow + CleanupInterval;
                    await CleanupAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogFailed(_logger, exception);
            }
        }
    }

    private async Task ProcessAsync(bool force, CancellationToken cancellationToken)
    {
        await _processing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _clock.UtcNow;
            var events = new List<FileEvent>();

            // Сначала итоги операций DiskApp: их следы отсеивают изменения индекса, которые они же и вызвали.
            Guid[] completed;
            IndexJournalEvent[] summaries;
            lock (_gate)
            {
                completed = [.. _completed];
                _completed.Clear();
                summaries = [.. _summaries];
                _summaries.Clear();
            }

            foreach (var id in completed)
            {
                if (_operations.Get(id) is { } info)
                {
                    events.AddRange(FromOperation(info, now));
                }
            }

            // Следы операций — идущие и завершившиеся недавно, прямо из очереди: так не важно, успело ли прийти событие
            // о завершении к моменту, когда разбираются изменения индекса.
            var operations = _operations.GetAll()
                .Where(info => info.IsActive || (info.CompletedUtc is { } done && now - done <= OperationGrace))
                .ToArray();
            events.AddRange(summaries.Select(summary => new FileEvent(0, now, FileEventKind.IndexSummary, FileEventSource.Reconcile, summary.RootPath ?? string.Empty, null, true, summary.Count, Details: summary.Summary)));
            events.AddRange(FromJournal(now, force, operations));
            if (events.Count > 0)
            {
                await SaveAsync(events, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _processing.Release();
        }
    }

    private async Task<IReadOnlyList<FileEvent>> SaveAsync(IReadOnlyList<FileEvent> events, CancellationToken cancellationToken)
    {
        var saved = await _repository.AddAsync(events, cancellationToken).ConfigureAwait(false);
        _events.Publish(new FileHistoryChangedEvent(saved.Count));
        return saved;
    }

    private IEnumerable<FileEvent> FromOperation(FileOperationInfo info, DateTimeOffset now)
    {
        var source = info.IsUndo ? FileEventSource.DiskApp : info.Origin switch
        {
            OperationOrigin.Sorting => FileEventSource.Sorting,
            OperationOrigin.AutoSort => FileEventSource.AutoSort,
            _ => FileEventSource.DiskApp,
        };

        foreach (var move in info.Moved)
        {
            var (isDirectory, size) = Stat(move.To);
            yield return new FileEvent(0, now, MoveKind(move.From, move.To), source, move.To, move.From, isDirectory, size, info.Id);
        }

        foreach (var deleted in info.Deleted)
        {
            yield return deleted.Permanent
                ? new FileEvent(0, now, FileEventKind.Deleted, source, deleted.Path, null, deleted.IsDirectory, deleted.Size, info.Id)
                : new FileEvent(0, now, FileEventKind.Recycled, source, deleted.Path, null, deleted.IsDirectory, deleted.Size, info.Id, FindRecycled(deleted.Path));
        }

        foreach (var created in info.Created)
        {
            var (isDirectory, size) = Stat(created);
            yield return new FileEvent(0, now, FileEventKind.Created, source, created, null, isDirectory, size, info.Id);
        }
    }

    private List<FileEvent> FromJournal(DateTimeOffset now, bool force, IReadOnlyList<FileOperationInfo> operations)
    {
        var result = new List<FileEvent>();
        var removals = new List<Pending>();
        lock (_gate)
        {
            _restored.RemoveAll(item => now - item.TimeUtc > OperationGrace);
            _pending.RemoveAll(item => IsOperations(item.Change, operations) || IsRestored(item.Change));
            DropTransient();

            foreach (var item in _pending.Where(item => force || now - item.ReceivedUtc >= MatchWindow).ToArray())
            {
                if (!_pending.Remove(item))
                {
                    // Уже ушло парой к другому изменению.
                    continue;
                }

                var change = item.Change;
                switch (change.Kind)
                {
                    case JournalChangeKind.Renamed:
                        result.Add(IsTemporary(change.OldPath)
                            ? Event(now, FileEventKind.Created, item.Source, change, null)
                            : Event(now, MoveKind(change.OldPath!, change.Path), item.Source, change, change.OldPath));
                        break;
                    case JournalChangeKind.Removed when Partner(item, JournalChangeKind.Added) is { } added:
                        _pending.Remove(added);
                        result.Add(Event(now, MoveKind(change.Path, added.Change.Path), added.Source, added.Change, change.Path));
                        break;
                    case JournalChangeKind.Added when Partner(item, JournalChangeKind.Removed) is { } removed:
                        _pending.Remove(removed);
                        result.Add(Event(now, MoveKind(removed.Change.Path, change.Path), item.Source, change, removed.Change.Path));
                        break;
                    case JournalChangeKind.Removed:
                        removals.Add(item);
                        break;
                    default:
                        result.Add(Event(now, FileEventKind.Created, item.Source, change, null));
                        break;
                }
            }
        }

        // Корзина читается с диска — вне блокировки: исчезнувшее без пары либо в корзине, либо удалено навсегда.
        foreach (var removal in removals)
        {
            var stored = FindRecycled(removal.Change.Path);
            result.Add(Event(now, stored is null ? FileEventKind.Deleted : FileEventKind.Recycled, removal.Source, removal.Change, null) with { Details = stored });
        }

        return result;
    }

    // Изменение пути, который трогает идущая операция DiskApp или тронула только что, — след самой операции. Идущая
    // трогает свои источники и всё в папке назначения; завершившаяся — то, что перенесла, удалила и создала.
    private bool IsRestored(JournalChange change)
        => _restored.Exists(item => _paths.Comparer.Equals(change.Path, item.Path) || _paths.IsNestedIn(change.Path, item.Path));

    private bool IsOperations(JournalChange change, IReadOnlyList<FileOperationInfo> operations)
    {
        bool Touches(string? path, string target)
            => path is not null && (_paths.Comparer.Equals(path, target) || _paths.IsNestedIn(path, target));

        bool Either(string target) => Touches(change.Path, target) || Touches(change.OldPath, target);

        foreach (var operation in operations)
        {
            if (operation.Sources.Any(Either)
                || operation.Moved.Any(move => Either(move.From) || Either(move.To))
                || operation.Deleted.Any(item => Either(item.Path))
                || operation.Created.Any(Either))
            {
                return true;
            }

            if (operation.IsActive && operation.Destination is { } destination
                && (_paths.IsNestedIn(change.Path, destination) || (change.OldPath is not null && _paths.IsNestedIn(change.OldPath, destination))))
            {
                return true;
            }
        }

        return false;
    }

    // Появилось и исчезло по тому же пути в одном окне — временный файл или перезапись: для истории ничего не случилось.
    private void DropTransient()
    {
        var added = _pending.Where(static item => item.Change.Kind == JournalChangeKind.Added).ToArray();
        foreach (var item in added)
        {
            if (_pending.FirstOrDefault(other => other.Change.Kind == JournalChangeKind.Removed && _paths.Comparer.Equals(other.Change.Path, item.Change.Path)) is { } removed)
            {
                _pending.Remove(item);
                _pending.Remove(removed);
            }
        }
    }

    // Пара к исчезнувшему (появившемуся) — появившееся (исчезнувшее) с тем же размером и временем изменения, ближайшее по
    // времени: у переноса в Проводнике дата изменения и размер сохраняются; имя или дата создания совпадают.
    private Pending? Partner(Pending item, JournalChangeKind kind)
        => _pending
            .Where(other => other.Change.Kind == kind && Same(item.Change, other.Change))
            .OrderBy(other => Math.Abs((other.ReceivedUtc - item.ReceivedUtc).Ticks))
            .FirstOrDefault();

    private bool Same(JournalChange left, JournalChange right)
    {
        if (left.IsDirectory != right.IsDirectory || _paths.Comparer.Equals(left.Path, right.Path))
        {
            return false;
        }

        var sameName = _paths.Comparer.Equals(_paths.GetFileName(left.Path), _paths.GetFileName(right.Path));
        if (left.IsDirectory)
        {
            return sameName;
        }

        return left.Size == right.Size
            && (left.ModifiedUtc - right.ModifiedUtc).Duration() <= TimeSpan.FromSeconds(2)
            && (sameName || (left.CreatedUtc - right.CreatedUtc).Duration() <= TimeSpan.FromSeconds(2));
    }

    private static FileEvent Event(DateTimeOffset now, FileEventKind kind, JournalSource source, JournalChange change, string? oldPath)
        => new(0, now, kind, source == JournalSource.Reconcile ? FileEventSource.Reconcile : FileEventSource.Explorer, change.Path, oldPath, change.IsDirectory, change.IsDirectory ? 0 : change.Size);

    private FileEventKind MoveKind(string from, string to)
        => _paths.Comparer.Equals(_paths.GetParent(from), _paths.GetParent(to)) ? FileEventKind.Renamed : FileEventKind.Moved;

    private static bool IsTemporary(string? path)
        => path is not null && TemporarySuffixes.Any(suffix => path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    private string? FindRecycled(string path)
    {
        try
        {
            return _recycleBin?.Find(path)?.StoredPath;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private bool Exists(string path) => _fileSystem.File.Exists(path) || _fileSystem.Directory.Exists(path);

    private (bool IsDirectory, long Size) Stat(string path)
    {
        try
        {
            return _fileSystem.Directory.Exists(path) ? (true, 0) : _fileSystem.File.Exists(path) ? (false, _fileSystem.FileInfo.New(path).Length) : (false, 0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (false, 0);
        }
    }

    private sealed record Pending(JournalChange Change, JournalSource Source, DateTimeOffset ReceivedUtc);
}

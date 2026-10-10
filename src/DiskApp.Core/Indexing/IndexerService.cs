using DiskApp.Core.Abstractions;
using DiskApp.Core.Domain;
using DiskApp.Core.Events;
using DiskApp.Core.FileSystem;
using DiskApp.Core.Indexing.Watching;
using DiskApp.Core.Repositories;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskApp.Core.Indexing;

/// <summary>
/// Оркестратор индексации (PLAN.md, этап 2.4). При старте загружает корни, запускает наблюдателей и для каждого корня
/// ставит полное сканирование (первый раз, после восстановления индекса) или сверку. Задачи выполняются по приоритету
/// (сверка поддерева по событию &gt; сверка корня &gt; полное сканирование), не больше одной на физический диск и одной
/// на корень. Недоступные корни переводятся в Offline без потери индекса и периодически проверяются; при возвращении —
/// сверка. Управление: пауза, возобновление, пересканирование корня, пересборка всего индекса.
/// Хост (сервер этапа 4) вызывает <see cref="StartAsync"/> и <see cref="DisposeAsync"/>.
/// </summary>
public sealed class IndexerService : IAsyncDisposable
{
    private readonly IRootRepository _roots;
    private readonly IFileIndexRepository _entries;
    private readonly IScanLogRepository _scanLog;
    private readonly IFileSystemLister _lister;
    private readonly IRootWatcherFactory _watchers;
    private readonly IDriveInfoProvider _drives;
    private readonly IIndexEventBus _events;
    private readonly IClock _clock;
    private readonly EntryFactory _factory;
    private readonly ILogger _logger;
    private readonly IndexerOptions _options;
    private readonly ScanPipeline _pipeline;
    private readonly ReconcileRunner _reconcile;
    private readonly ChangeApplier _applier;
    private readonly PauseTokenSource _pause = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<long, RootSession> _sessions = [];
    private readonly IndexTaskBacklog _queue;
    private readonly HashSet<string> _busyDisks = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<long> _busyRoots = [];
    private readonly List<Task> _running = [];
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly IDisposable _progressSubscription;
    private TaskCompletionSource _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _dispatcher;
    private Task? _scheduler;

    /// <summary>
    /// Создаёт оркестратор; работа начинается в <see cref="StartAsync"/>.
    /// </summary>
    public IndexerService(
        IRootRepository roots,
        IFileIndexRepository entries,
        IScanLogRepository scanLog,
        IFileSystemLister lister,
        IRootWatcherFactory watchers,
        IDriveInfoProvider drives,
        IIndexEventBus events,
        IClock clock,
        EntryFactory factory,
        IndexerOptions options,
        IBackgroundPriority? priority = null,
        ILogger<IndexerService>? logger = null)
    {
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _entries = entries ?? throw new ArgumentNullException(nameof(entries));
        _scanLog = scanLog ?? throw new ArgumentNullException(nameof(scanLog));
        _lister = lister ?? throw new ArgumentNullException(nameof(lister));
        _watchers = watchers ?? throw new ArgumentNullException(nameof(watchers));
        _drives = drives ?? throw new ArgumentNullException(nameof(drives));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _queue = new IndexTaskBacklog(factory.Paths);
        _pipeline = new ScanPipeline(new DirectoryScanner(lister, factory, priority), entries, scanLog, events, clock, factory);
        _reconcile = new ReconcileRunner(lister, entries, scanLog, events, clock, factory);
        _applier = new ChangeApplier(lister, entries, events, factory);
        _progressSubscription = events.Subscribe<IndexProgressEvent>(OnProgress);
        _idle.TrySetResult();
    }

    /// <summary>Пауза включена.</summary>
    public bool IsPaused => _pause.IsPaused;

    /// <summary>
    /// Загружает корни, запускает наблюдателей, ставит начальные задачи и запускает диспетчер и планировщик.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_dispatcher is not null)
        {
            throw new InvalidOperationException("Индексатор уже запущен.");
        }

        var roots = await _roots.GetAllAsync(cancellationToken).ConfigureAwait(false);
        _dispatcher = Task.Run(() => DispatchLoopAsync(_stop.Token), CancellationToken.None);
        _scheduler = Task.Run(() => SchedulerLoopAsync(_stop.Token), CancellationToken.None);

        foreach (var root in roots.Where(static root => root.IsEnabled))
        {
            await AttachAsync(root, initial: true).ConfigureAwait(false);
        }

        IndexerLog.Started(_logger, roots.Count);
    }

    /// <summary>
    /// Сверяет набор корней с репозиторием: новые и включённые подключаются, удалённые и выключенные отключаются,
    /// корни с изменёнными параметрами переподключаются со сверкой.
    /// </summary>
    public async Task RefreshRootsAsync(CancellationToken cancellationToken = default)
    {
        var roots = (await _roots.GetAllAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(static root => root.Id);
        List<RootSession> toDetach;
        List<Root> toAttach;
        lock (_gate)
        {
            toDetach = _sessions.Values
                .Where(session => !roots.TryGetValue(session.Root.Id, out var root) || !root.IsEnabled || !SameIndexingParameters(session.Root, root))
                .ToList();
            var detachedIds = toDetach.Select(static session => session.Root.Id).ToHashSet();
            toAttach = roots.Values
                .Where(root => root.IsEnabled && (!_sessions.ContainsKey(root.Id) || detachedIds.Contains(root.Id)))
                .ToList();
        }

        foreach (var session in toDetach)
        {
            await DetachAsync(session).ConfigureAwait(false);
        }

        foreach (var root in toAttach)
        {
            await AttachAsync(root, initial: false).ConfigureAwait(false);
        }
    }

    /// <summary>Приостанавливает индексацию: задачи замирают между папками, события наблюдателей копятся.</summary>
    public void PauseAll()
    {
        _pause.Pause();
        foreach (var session in Sessions())
        {
            if (session.Status is RootStatus.Online or RootStatus.Scanning)
            {
                _ = SetStatusAsync(session, RootStatus.Paused, null);
            }
        }

        IndexerLog.Paused(_logger);
    }

    /// <summary>Возобновляет индексацию.</summary>
    public void ResumeAll()
    {
        _pause.Resume();
        foreach (var session in Sessions())
        {
            if (session.Status == RootStatus.Paused)
            {
                _ = SetStatusAsync(session, session.RunningKind is null ? RootStatus.Online : RootStatus.Scanning, null);
            }
        }

        _wake.Release();
        IndexerLog.Resumed(_logger);
    }

    /// <summary>Ставит полное сканирование корня.</summary>
    public bool RescanRoot(long rootId) => Enqueue(new IndexTask(rootId, IndexTaskKind.FullScan, string.Empty, "Пересканирование по запросу"));

    /// <summary>Ставит сверку корня или поддерева.</summary>
    public bool ReconcileRoot(long rootId, string relPath = "") => Enqueue(new IndexTask(rootId, IndexTaskKind.Reconcile, relPath, "Сверка по запросу"));

    /// <summary>
    /// Пересобирает индекс: удаляет записи всех корней и ставит полное сканирование каждого.
    /// </summary>
    public async Task RebuildAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var session in Sessions())
        {
            await _entries.DeleteByRootAsync(session.Root.Id, cancellationToken).ConfigureAwait(false);
            Enqueue(new IndexTask(session.Root.Id, IndexTaskKind.FullScan, string.Empty, "Пересборка индекса"));
        }
    }

    /// <summary>Состояние каждого подключённого корня.</summary>
    public IReadOnlyList<RootIndexStatus> GetStatuses()
    {
        lock (_gate)
        {
            return _sessions.Values
                .OrderBy(static session => session.Root.Id)
                .Select(session => new RootIndexStatus(
                    session.Root.Id,
                    session.Root.Path,
                    session.Root.Label,
                    session.Status,
                    session.Message,
                    session.RunningKind,
                    _queue.HasPendingFor(session.Root.Id),
                    session.Progress,
                    session.Root.LastScanUtc))
                .ToArray();
        }
    }

    /// <summary>Общее состояние для иконки трея.</summary>
    public IndexerState GetAggregateState()
    {
        var statuses = GetStatuses();
        if (statuses.Any(static status => status.Status == RootStatus.Error))
        {
            return IndexerState.Error;
        }

        if (IsPaused)
        {
            return IndexerState.Paused;
        }

        if (statuses.Any(static status => status.RunningTask is not null || status.IsQueued))
        {
            return IndexerState.Indexing;
        }

        return statuses.Any(static status => status.Status == RootStatus.Offline) ? IndexerState.PartiallyOffline : IndexerState.Idle;
    }

    /// <summary>
    /// Завершается, когда очередь пуста и ни одна задача не выполняется; для тестов и пересборки.
    /// </summary>
    public Task WhenIdleAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return _idle.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Останавливает диспетчер, планировщик, наблюдателей и выполняющиеся задачи.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _pause.Resume();
        _progressSubscription.Dispose();

        foreach (var task in new[] { _dispatcher, _scheduler }.OfType<Task>())
        {
            await SwallowAsync(task).ConfigureAwait(false);
        }

        Task[] running;
        lock (_gate)
        {
            running = [.. _running];
        }

        foreach (var task in running)
        {
            await SwallowAsync(task).ConfigureAwait(false);
        }

        foreach (var session in Sessions())
        {
            if (session.Processor is { } processor)
            {
                await processor.DisposeAsync().ConfigureAwait(false);
            }
        }

        _wake.Dispose();
        _stop.Dispose();
    }

    private static bool SameIndexingParameters(Root left, Root right)
        => string.Equals(left.Path, right.Path, StringComparison.Ordinal)
            && left.IncludeHidden == right.IncludeHidden
            && left.IncludeSystem == right.IncludeSystem
            && left.MaxDepth == right.MaxDepth;

    private static async Task SwallowAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Остановка: ошибки задач уже записаны в журнал.
        }
    }

    private RootSession[] Sessions()
    {
        lock (_gate)
        {
            return [.. _sessions.Values];
        }
    }

    private async Task AttachAsync(Root root, bool initial)
    {
        var drive = _drives.Describe(root.Path);
        var session = new RootSession(root, new RootIndexState(_factory.Paths), ExclusionMatcher.Create(_options.Exclusions, root, _factory.Paths))
        {
            DiskKey = drive.DiskKey,
            Parallelism = _options.MaxScanParallelism > 0 ? _options.MaxScanParallelism : drive.RecommendedParallelism,
            IsNetwork = drive.Media == DriveMedia.Network,
            Status = root.Status,
        };

        lock (_gate)
        {
            _sessions[root.Id] = session;
        }

        if (!_lister.DirectoryExists(root.Path))
        {
            await GoOfflineAsync(session, "Корень недоступен при запуске.").ConfigureAwait(false);
            return;
        }

        await BringOnlineAsync(session, initial).ConfigureAwait(false);
    }

    private async Task DetachAsync(RootSession session)
    {
        lock (_gate)
        {
            _sessions.Remove(session.Root.Id);
            _queue.RemoveRoot(session.Root.Id);
        }

        session.Detached = true;
        if (session.Processor is { } processor)
        {
            session.Processor = null;
            await processor.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task BringOnlineAsync(RootSession session, bool initial)
    {
        StartWatcher(session);
        await SetStatusAsync(session, _pause.IsPaused ? RootStatus.Paused : RootStatus.Online, null).ConfigureAwait(false);

        var needsFullScan = session.Root.LastScanUtc is null || (initial && _options.ForceFullScan);
        Enqueue(needsFullScan
            ? new IndexTask(session.Root.Id, IndexTaskKind.FullScan, string.Empty, session.Root.LastScanUtc is null ? "Первое сканирование" : "Индекс восстановлен")
            : new IndexTask(session.Root.Id, IndexTaskKind.Reconcile, string.Empty, initial ? "Сверка при запуске" : "Корень снова доступен"));
    }

    private void StartWatcher(RootSession session)
    {
        if (session.Processor is not null)
        {
            return;
        }

        try
        {
            var processor = new RootChangeProcessor(
                session.Root,
                _watchers.Create(session.Root),
                _applier,
                () => session.Matcher,
                session.State,
                new Watching.ChangeCoalescer(_factory.Paths, _options.WatcherQuiet, _options.WatcherMaxDelay),
                _clock,
                _pause.Token,
                OnRootRequest);
            processor.Start();
            session.Processor = processor;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Без наблюдателя корень индексируется только сверками; например, на некоторых сетевых ресурсах.
            IndexerLog.WatcherNotStarted(_logger, exception, session.Root.Path);
        }
    }

    private async Task GoOfflineAsync(RootSession session, string reason)
    {
        if (session.Processor is { } processor)
        {
            session.Processor = null;
            await processor.DisposeAsync().ConfigureAwait(false);
        }

        session.LastAvailabilityCheck = _clock.UtcNow;
        await SetStatusAsync(session, RootStatus.Offline, reason).ConfigureAwait(false);
        IndexerLog.RootOffline(_logger, session.Root.Path, reason);
    }

    private void OnRootRequest(RootRequest request)
    {
        switch (request.Kind)
        {
            case RootRequestKind.Reconcile:
                Enqueue(new IndexTask(request.RootId, IndexTaskKind.Reconcile, request.RelPath, request.Reason));
                break;
            case RootRequestKind.CheckAvailability:
                _ = Task.Run(() => CheckAvailabilityAsync(request.RootId, request.Reason));
                break;
            default:
                break;
        }
    }

    private async Task CheckAvailabilityAsync(long rootId, string reason)
    {
        RootSession? session;
        lock (_gate)
        {
            _sessions.TryGetValue(rootId, out session);
        }

        if (session is null || session.Detached || session.Status == RootStatus.Offline)
        {
            return;
        }

        if (!_lister.DirectoryExists(session.Root.Path))
        {
            await GoOfflineAsync(session, reason).ConfigureAwait(false);
            return;
        }

        // Наблюдатель упал, но корень на месте: перезапускаем его и догоняем пропущенное сверкой.
        if (session.Processor is { } processor)
        {
            session.Processor = null;
            await processor.DisposeAsync().ConfigureAwait(false);
        }

        StartWatcher(session);
        Enqueue(new IndexTask(rootId, IndexTaskKind.Reconcile, string.Empty, "Наблюдатель перезапущен: " + reason));
    }

    private bool Enqueue(IndexTask task)
    {
        bool added;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(task.RootId, out var session) || session.Status == RootStatus.Offline)
            {
                return false;
            }

            added = _queue.Enqueue(task);
            if (added && _idle.Task.IsCompleted)
            {
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        if (added)
        {
            _wake.Release();
        }

        return added;
    }

    private async Task DispatchLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _wake.WaitAsync(cancellationToken).ConfigureAwait(false);
            StartReadyTasks(cancellationToken);
        }
    }

    private void StartReadyTasks(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            while (_running.Count < Math.Max(1, _options.MaxConcurrentTasks) && !_pause.IsPaused)
            {
                var task = _queue.TryDequeue(CanRun);
                if (task is null)
                {
                    break;
                }

                var session = _sessions[task.RootId];
                _busyDisks.Add(session.DiskKey);
                _busyRoots.Add(task.RootId);
                session.RunningKind = task.Kind;
                Task running = null!;
                running = Task.Run(() => RunTaskAsync(session, task, running, cancellationToken), CancellationToken.None);
                _running.Add(running);
            }

            if (_queue.Count == 0 && _running.Count == 0)
            {
                _idle.TrySetResult();
            }
        }
    }

    private bool CanRun(IndexTask task)
        => _sessions.TryGetValue(task.RootId, out var session)
            && !_busyRoots.Contains(task.RootId)
            && !_busyDisks.Contains(session.DiskKey);

    private async Task RunTaskAsync(RootSession session, IndexTask task, Task self, CancellationToken cancellationToken)
    {
        // Гарантирует, что ссылка на эту задачу уже записана в _running до первой точки ожидания.
        await Task.Yield();
        try
        {
            await SetStatusAsync(session, _pause.IsPaused ? RootStatus.Paused : RootStatus.Scanning, null).ConfigureAwait(false);
            var request = new ScanRequest
            {
                Root = session.Root,
                RelPath = task.RelPath,
                Matcher = session.Matcher,
                DegreeOfParallelism = session.Parallelism,
                FollowReparsePoints = _options.FollowReparsePoints,
                BatchSize = _options.BatchSize,
            };

            IndexerLog.TaskStarted(_logger, session.Root.Path, task.RelPath, task.Kind, task.Reason);
            var outcome = task.Kind == IndexTaskKind.FullScan
                ? await RunFullScanAsync(session, request, cancellationToken).ConfigureAwait(false)
                : await _reconcile.RunAsync(request, _pause.Token, cancellationToken).ConfigureAwait(false);

            IndexerLog.TaskCompleted(_logger, session.Root.Path, task.RelPath, outcome.Status, outcome.Found, outcome.Written.Inserted, outcome.Written.Updated, outcome.Removed, outcome.ErrorCount, (long)outcome.Elapsed.TotalMilliseconds);

            if (outcome.Status == ScanOutcomeStatus.RootMissing)
            {
                await GoOfflineAsync(session, "Корень пропал во время индексации.").ConfigureAwait(false);
            }
            else
            {
                if (task.RelPath.Length == 0)
                {
                    var now = _clock.UtcNow;
                    await _roots.SetLastScanAsync(session.Root.Id, now, cancellationToken).ConfigureAwait(false);
                    session.Root = session.Root with { LastScanUtc = now };
                }

                session.RunningKind = null;
                await SetStatusAsync(session, _pause.IsPaused ? RootStatus.Paused : RootStatus.Online, null).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Остановка сервиса.
        }
        catch (Exception exception)
        {
            IndexerLog.TaskFailed(_logger, exception, session.Root.Path);
            session.RunningKind = null;
            await SetStatusAsync(session, RootStatus.Error, exception.Message).ConfigureAwait(false);
            await TryLogAsync(session.Root.Id, ScanLogLevel.Error, task.RelPath, exception.Message).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                session.RunningKind = null;
                _busyDisks.Remove(session.DiskKey);
                _busyRoots.Remove(session.Root.Id);
                _running.Remove(self);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                _wake.Release();
            }
        }
    }

    private async Task<ScanOutcome> RunFullScanAsync(RootSession session, ScanRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await _pipeline.RunAsync(request, _pause.Token, session.State.BeginScan, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            var touched = session.State.EndScan();
            if (touched.Count > 0)
            {
                session.Processor?.Recheck(touched);
            }
        }
    }

    private async Task SchedulerLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.SchedulerTick);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var now = _clock.UtcNow;
            foreach (var session in Sessions())
            {
                if (session.Detached)
                {
                    continue;
                }

                if (session.Status == RootStatus.Offline)
                {
                    if (now - session.LastAvailabilityCheck >= _options.OfflinePollInterval)
                    {
                        session.LastAvailabilityCheck = now;
                        if (_lister.DirectoryExists(session.Root.Path))
                        {
                            IndexerLog.RootBackOnline(_logger, session.Root.Path);
                            await BringOnlineAsync(session, initial: false).ConfigureAwait(false);
                        }
                    }

                    continue;
                }

                var interval = session.IsNetwork ? _options.ReconcileInterval / 4 : _options.ReconcileInterval;
                if (interval > TimeSpan.Zero && session.Root.LastScanUtc is { } last && now - last >= interval)
                {
                    Enqueue(new IndexTask(session.Root.Id, IndexTaskKind.Reconcile, string.Empty, "Плановая сверка"));
                }
            }
        }
    }

    private async Task SetStatusAsync(RootSession session, RootStatus status, string? message)
    {
        if (session.Status == status && string.Equals(session.Message, message, StringComparison.Ordinal))
        {
            return;
        }

        session.Status = status;
        session.Message = message;
        try
        {
            await _roots.SetStatusAsync(session.Root.Id, status, message).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            IndexerLog.StatusNotSaved(_logger, exception, session.Root.Path);
        }

        _events.Publish(new RootStatusChangedEvent(session.Root.Id, status, message));
    }

    private async Task TryLogAsync(long rootId, ScanLogLevel level, string relPath, string message)
    {
        try
        {
            await _scanLog.AddAsync([new ScanLogEntry(rootId, 0, _clock.UtcNow, level, relPath.Length == 0 ? null : relPath, message)]).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            IndexerLog.ScanLogFailed(_logger, exception);
        }
    }

    private void OnProgress(IndexProgressEvent progress)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(progress.RootId, out var session))
            {
                session.Progress = progress;
            }
        }
    }

    private sealed class RootSession(Root root, RootIndexState state, ExclusionMatcher matcher)
    {
        public Root Root { get; set; } = root;

        public RootIndexState State { get; } = state;

        public ExclusionMatcher Matcher { get; } = matcher;

        public string DiskKey { get; init; } = string.Empty;

        public int Parallelism { get; init; } = 1;

        public bool IsNetwork { get; init; }

        public RootStatus Status { get; set; }

        public string? Message { get; set; }

        public IndexTaskKind? RunningKind { get; set; }

        public IndexProgressEvent? Progress { get; set; }

        public RootChangeProcessor? Processor { get; set; }

        public DateTimeOffset LastAvailabilityCheck { get; set; }

        public bool Detached { get; set; }
    }
}

/// <summary>
/// Состояние индексации корня для интерфейса и API.
/// </summary>
/// <param name="RootId">Корень.</param>
/// <param name="Path">Путь.</param>
/// <param name="Label">Подпись.</param>
/// <param name="Status">Состояние.</param>
/// <param name="Message">Пояснение для ошибки и недоступности.</param>
/// <param name="RunningTask">Выполняющаяся задача; <c>null</c>, если нет.</param>
/// <param name="IsQueued">Есть ожидающие задачи.</param>
/// <param name="Progress">Последнее событие прогресса.</param>
/// <param name="LastScanUtc">Когда корень последний раз сверялся с диском целиком.</param>
public sealed record RootIndexStatus(
    long RootId,
    string Path,
    string Label,
    RootStatus Status,
    string? Message,
    IndexTaskKind? RunningTask,
    bool IsQueued,
    IndexProgressEvent? Progress,
    DateTimeOffset? LastScanUtc);

/// <summary>
/// Общее состояние индексации для иконки трея (PLAN.md, этап 2.4).
/// </summary>
public enum IndexerState
{
    /// <summary>Индекс актуален.</summary>
    Idle,

    /// <summary>Идёт сканирование или сверка.</summary>
    Indexing,

    /// <summary>Индексация на паузе.</summary>
    Paused,

    /// <summary>Часть корней недоступна, остальные актуальны.</summary>
    PartiallyOffline,

    /// <summary>У какого-то корня ошибка.</summary>
    Error,
}

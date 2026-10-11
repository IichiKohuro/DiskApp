using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO.Abstractions;
using System.Threading.Channels;

using DiskApp.Core.Abstractions;
using DiskApp.Core.Events;
using DiskApp.Core.Indexing;
using DiskApp.Core.Localization;
using DiskApp.Core.Paths;
using DiskApp.Core.Results;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskApp.Core.Operations;

/// <summary>
/// Очередь файловых операций (PLAN.md, этап 4.5): копирование, перемещение, переименование, удаление, создание.
/// Операции выполняются по одной в порядке постановки. Копирование — потоками с большим буфером, с сохранением
/// атрибутов и времён; перемещение в пределах тома — переименованием, между томами — копированием и удалением
/// источника после проверки. Конфликты имён решаются политикой или пользователем (операция ждёт решения).
/// Удаление — в корзину через <see cref="IPlatformShell"/>; безвозвратно — только по явному флагу. Существующий файл
/// при замене тоже уходит в корзину. Переименование, перемещение и создание можно отменить, пока жив сервис.
/// После операции затронутые папки сверяются с индексом сразу (<see cref="IPathChangeSink"/>).
/// </summary>
public sealed partial class FileOperationService : IAsyncDisposable
{
    /// <summary>Код ошибки: неверный запрос.</summary>
    public const string InvalidRequestCode = "operation.invalid";

    /// <summary>Код ошибки: операции нет.</summary>
    public const string NotFoundCode = "operation.not_found";

    /// <summary>Код ошибки: операция не ждёт решения.</summary>
    public const string NoConflictCode = "operation.no_conflict";

    /// <summary>Код ошибки: операцию нельзя отменить.</summary>
    public const string NotUndoableCode = "operation.not_undoable";

    /// <summary>Код ошибки: операция уже завершена.</summary>
    public const string FinishedCode = "operation.finished";

    private static readonly char[] InvalidNameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|', .. Enumerable.Range(0, 32).Select(static c => (char)c)];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly IFileSystem _fs;
    private readonly IIndexEventBus _events;
    private readonly IClock _clock;
    private readonly PathNormalizer _paths;
    private readonly FileOperationOptions _options;
    private readonly IPlatformShell? _shell;
    private readonly IPathChangeSink? _changes;
    private readonly ILogger _logger;
    private readonly Channel<OperationRun> _queue = Channel.CreateUnbounded<OperationRun>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _gate = new();
    private readonly List<OperationRun> _runs = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private TaskCompletionSource _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _pending;
    private int _disposed;

    /// <summary>
    /// Создаёт сервис и запускает обработчик очереди.
    /// </summary>
    public FileOperationService(
        IFileSystem fileSystem,
        IIndexEventBus events,
        IClock clock,
        PathNormalizer paths,
        FileOperationOptions? options = null,
        IPlatformShell? shell = null,
        IPathChangeSink? changes = null,
        ILogger<FileOperationService>? logger = null)
    {
        _fs = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _options = options ?? new FileOperationOptions();
        _shell = shell;
        _changes = changes;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _idle.TrySetResult();
        _worker = Task.Run(WorkAsync);
    }

    /// <summary>
    /// Проверяет запрос и ставит операцию в очередь.
    /// </summary>
    public Result<FileOperationInfo> Enqueue(FileOperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        if (Validate(request, out var normalized) is { } error)
        {
            return Result.Fail<FileOperationInfo>(error);
        }

        return Result.Ok(Start(new OperationRun(Guid.NewGuid(), normalized, _clock.UtcNow, _stop.Token)));
    }

    /// <summary>Операции: незавершённые и последние завершённые, новые первыми.</summary>
    public IReadOnlyList<FileOperationInfo> GetAll()
    {
        lock (_gate)
        {
            return _runs.AsEnumerable().Reverse().Select(Snapshot).ToArray();
        }
    }

    /// <summary>Незавершённые операции.</summary>
    public IReadOnlyList<FileOperationInfo> GetActive() => GetAll().Where(static info => info.IsActive).ToArray();

    /// <summary>Операция по идентификатору.</summary>
    public FileOperationInfo? Get(Guid id)
    {
        lock (_gate)
        {
            return Find(id) is { } run ? Snapshot(run) : null;
        }
    }

    /// <summary>
    /// Отменяет операцию: в очереди — сразу, выполняющуюся — на ближайшей границе блока данных; недокопированный файл удаляется.
    /// </summary>
    public Result Cancel(Guid id)
    {
        OperationRun? run;
        lock (_gate)
        {
            run = Find(id);
            if (run is null)
            {
                return Result.Fail(new DomainError(NotFoundCode, L.T("Operations.NotFound")));
            }

            if (IsFinished(run.State))
            {
                return Result.Fail(new DomainError(FinishedCode, L.T("FileOperation.TheOperationHasAlreadyFinished")));
            }
        }

        run.Cancellation.Cancel();
        run.Decision?.TrySetResult((ConflictResolution.Cancel, false));
        return Result.Ok();
    }

    /// <summary>
    /// Решение по конфликту, которого ждёт операция.
    /// </summary>
    /// <param name="id">Операция.</param>
    /// <param name="resolution">Что сделать с элементом.</param>
    /// <param name="applyToAll">Применить ко всем следующим конфликтам этой операции.</param>
    public Result Decide(Guid id, ConflictResolution resolution, bool applyToAll)
    {
        lock (_gate)
        {
            var run = Find(id);
            if (run is null)
            {
                return Result.Fail(new DomainError(NotFoundCode, L.T("Operations.NotFound")));
            }

            if (run.Decision is not { } decision || run.PendingConflict is null)
            {
                return Result.Fail(new DomainError(NoConflictCode, L.T("FileOperation.TheOperationIsNotWaitingFor")));
            }

            decision.TrySetResult((resolution, applyToAll));
            return Result.Ok();
        }
    }

    /// <summary>
    /// Отменяет результат переименования, перемещения или создания: ставит обратную операцию.
    /// </summary>
    public Result<FileOperationInfo> Undo(Guid id)
    {
        OperationRun undo;
        lock (_gate)
        {
            var run = Find(id);
            if (run is null)
            {
                return Result.Fail<FileOperationInfo>(new DomainError(NotFoundCode, L.T("Operations.NotFound")));
            }

            if (!CanUndo(run))
            {
                return Result.Fail<FileOperationInfo>(new DomainError(NotUndoableCode, L.T("FileOperation.ThisOperationCannotBeUndone")));
            }

            run.Undone = true;
            undo = run.Request.Kind is FileOperationKind.CreateFolder or FileOperationKind.CreateFile
                ? new OperationRun(Guid.NewGuid(), new FileOperationRequest(FileOperationKind.Delete, [run.Created!]), _clock.UtcNow, _stop.Token) { IsUndo = true }
                : new OperationRun(
                    Guid.NewGuid(),
                    new FileOperationRequest(FileOperationKind.Move, run.Moves.Select(static move => move.To).ToArray(), run.Request.Kind == FileOperationKind.Rename ? null : run.Request.Sources.Count > 0 ? _paths.GetParent(run.Request.Sources[0]) : null),
                    _clock.UtcNow,
                    _stop.Token)
                {
                    IsUndo = true,
                    UndoMoves = run.Moves.Select(static move => (move.To, move.From)).ToArray(),
                };
        }

        return Result.Ok(Start(undo));
    }

    /// <summary>Завершается, когда очередь пуста и ничего не выполняется.</summary>
    public Task WhenIdleAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return _idle.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Отменяет выполняющиеся операции и останавливает обработчик.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        _queue.Writer.TryComplete();
        lock (_gate)
        {
            foreach (var run in _runs)
            {
                run.Decision?.TrySetResult((ConflictResolution.Cancel, false));
            }
        }

        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Остановка.
        }

        lock (_gate)
        {
            foreach (var run in _runs)
            {
                run.Cancellation.Dispose();
            }
        }

        _stop.Dispose();
    }

    /// <summary>
    /// Почему имя файла или папки недопустимо; <c>null</c> — допустимо.
    /// </summary>
    public static string? NameProblem(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return L.T("FileOperation.TheNameCannotBeEmpty");
        }

        if (name is "." or "..")
        {
            return L.T("FileOperation.InvalidName");
        }

        if (name.IndexOfAny(InvalidNameChars) >= 0)
        {
            return L.T("FileOperation.TheNameCannotContain");
        }

        if (name.EndsWith('.') || name.EndsWith(' ') || name.StartsWith(' '))
        {
            return L.T("FileOperation.TheNameCannotStartWithA");
        }

        var dot = name.IndexOf('.', StringComparison.Ordinal);
        if (ReservedNames.Contains(dot < 0 ? name : name[..dot]))
        {
            return L.F("FileOperation.TheNameIsReservedByWindows", name);
        }

        return name.Length > 255 ? L.T("FileOperation.TheNameIsLongerThan255") : null;
    }

    private static bool IsFinished(OperationState state) => state is OperationState.Completed or OperationState.Failed or OperationState.Cancelled;

    private static bool CanUndo(OperationRun run)
        => run.State == OperationState.Completed
            && !run.Undone
            && !run.IsUndo
            && (run.Request.Kind switch
            {
                FileOperationKind.Rename or FileOperationKind.Move => run.Moves.Count > 0,
                FileOperationKind.CreateFolder or FileOperationKind.CreateFile => run.Created is not null,
                _ => false,
            });

    private FileOperationInfo Start(OperationRun run)
    {
        lock (_gate)
        {
            _runs.Add(run);
            TrimHistory();
            if (_pending++ == 0)
            {
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            _queue.Writer.TryWrite(run);
            return Snapshot(run);
        }
    }

    private void TrimHistory()
    {
        var finished = _runs.Count(static run => IsFinished(run.State));
        for (var i = 0; i < _runs.Count && finished > _options.HistoryLimit;)
        {
            if (IsFinished(_runs[i].State))
            {
                _runs[i].Cancellation.Dispose();
                _runs.RemoveAt(i);
                finished--;
            }
            else
            {
                i++;
            }
        }
    }

    private OperationRun? Find(Guid id) => _runs.Find(run => run.Id == id);

    private FileOperationInfo Snapshot(OperationRun run)
    {
        var elapsed = run.Clock.Elapsed.TotalSeconds;
        var speed = elapsed > 0.05 ? run.ProcessedBytes / elapsed : 0;
        TimeSpan? remaining = speed > 0 && run.TotalBytes > run.ProcessedBytes && run.State == OperationState.Running
            ? TimeSpan.FromSeconds((run.TotalBytes - run.ProcessedBytes) / speed)
            : null;
        return new FileOperationInfo(
            run.Id,
            run.Request.Kind,
            run.State,
            run.Request.Sources,
            run.Request.Destination,
            run.CreatedUtc,
            run.CompletedUtc,
            new OperationProgress(run.ProcessedItems, run.TotalItems, run.ProcessedBytes, run.TotalBytes, run.CurrentPath, Math.Round(speed, 1), remaining),
            run.Message,
            CanUndo(run),
            run.PendingConflict)
        {
            // Пока операция идёт, список переносов пополняется на её потоке; читается только после завершения.
            Moved = run.CompletedUtc is null ? [] : [.. run.Moves.Select(static move => new MovedItem(move.From, move.To))],
        };
    }

    private DomainError? Validate(FileOperationRequest request, out FileOperationRequest normalized)
    {
        normalized = request;
        var sources = new List<string>();
        foreach (var source in request.Sources ?? [])
        {
            if (!_paths.TryNormalize(source, out var path))
            {
                return Invalid(L.F("Operations.PathMustBeAbsolute", source));
            }

            if (_paths.IsRoot(path))
            {
                return Invalid(L.F("FileOperation.ThisCannotBeDoneWithThe", path));
            }

            if (!_fs.File.Exists(path) && !_fs.Directory.Exists(path))
            {
                return Invalid(L.F("FileOperation.NotFound", path));
            }

            sources.Add(path);
        }

        if (sources.Distinct(_paths.Comparer).Count() != sources.Count)
        {
            return Invalid(L.T("FileOperation.PathsRepeat"));
        }

        string? destination = null;
        if (request.Destination is not null)
        {
            if (!_paths.TryNormalize(request.Destination, out destination))
            {
                return Invalid(L.F("Operations.PathMustBeAbsolute", request.Destination));
            }

            if (!_fs.Directory.Exists(destination))
            {
                return Invalid(L.F("FileOperation.FolderNotFound", destination));
            }
        }

        switch (request.Kind)
        {
            case FileOperationKind.Copy or FileOperationKind.Move:
                if (sources.Count == 0 || destination is null)
                {
                    return Invalid(L.T("FileOperation.SayWhatToMoveAndInto"));
                }

                foreach (var source in sources.Where(_fs.Directory.Exists))
                {
                    if (_paths.Comparer.Equals(destination, source) || _paths.IsNestedIn(destination, source))
                    {
                        return Invalid(L.F("FileOperation.FolderCannotBePlacedIntoItself", source));
                    }
                }

                break;
            case FileOperationKind.Rename:
                if (sources.Count != 1)
                {
                    return Invalid(L.T("FileOperation.OnlyOneFileOrFolderCan"));
                }

                if (NameProblem(request.NewName) is { } renameProblem)
                {
                    return Invalid(renameProblem);
                }

                break;
            case FileOperationKind.Delete:
                if (sources.Count == 0)
                {
                    return Invalid(L.T("FileOperation.SayWhatToDelete"));
                }

                break;
            case FileOperationKind.CreateFolder or FileOperationKind.CreateFile:
                if (destination is null)
                {
                    return Invalid(L.T("FileOperation.SayInWhichFolderToCreate"));
                }

                if (NameProblem(request.NewName) is { } createProblem)
                {
                    return Invalid(createProblem);
                }

                break;
            default:
                return Invalid(L.T("FileOperation.UnknownOperation"));
        }

        normalized = request with { Sources = sources, Destination = destination, NewName = request.NewName?.Trim() };
        return null;

        static DomainError Invalid(string message) => new(InvalidRequestCode, message);
    }

    private async Task WorkAsync()
    {
        try
        {
            await foreach (var run in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                await RunAsync(run).ConfigureAwait(false);
                lock (_gate)
                {
                    if (--_pending == 0)
                    {
                        _idle.TrySetResult();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Остановка сервиса.
        }
    }

    private async Task RunAsync(OperationRun run)
    {
        var token = run.Cancellation.Token;
        var context = new RunContext(run, new HashSet<string>(_paths.Comparer), []);
        if (token.IsCancellationRequested)
        {
            Finish(run, OperationState.Cancelled, L.T("FileOperation.CancelledBeforeStart"));
            return;
        }

        lock (_gate)
        {
            run.State = OperationState.Running;
            run.Clock.Start();
        }

        LogStarted(_logger, run.Id, run.Request.Kind, run.Request.Sources.Count);
        try
        {
            switch (run.Request.Kind)
            {
                case FileOperationKind.Copy:
                    await CopyAllAsync(context, move: false, token).ConfigureAwait(false);
                    break;
                case FileOperationKind.Move when run.UndoMoves is not null:
                    await MovePairsAsync(context, run.UndoMoves, token).ConfigureAwait(false);
                    break;
                case FileOperationKind.Move:
                    await MoveAllAsync(context, token).ConfigureAwait(false);
                    break;
                case FileOperationKind.Rename:
                    await RenameAsync(context, token).ConfigureAwait(false);
                    break;
                case FileOperationKind.Delete:
                    await DeleteAsync(context, token).ConfigureAwait(false);
                    break;
                case FileOperationKind.CreateFolder or FileOperationKind.CreateFile:
                    await CreateAsync(context, token).ConfigureAwait(false);
                    break;
            }

            Finish(run, context.Errors.Count == 0 ? OperationState.Completed : OperationState.Failed, Summary(context));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Finish(run, OperationState.Cancelled, _stop.IsCancellationRequested ? L.T("FileOperation.StoppedWhenTheAppExited") : L.T("FileOperation.Cancelled"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Finish(run, OperationState.Failed, exception.Message);
        }
        finally
        {
            if (context.Affected.Count > 0)
            {
                _changes?.OnPathsChanged(context.Affected);
            }
        }
    }

    private static string? Summary(RunContext context)
    {
        if (context.Errors.Count == 0)
        {
            return context.Skipped > 0 ? L.F("FileOperation.Skipped", context.Skipped.ToString(CultureInfo.InvariantCulture)) : null;
        }

        return context.Errors.Count == 1
            ? context.Errors[0]
            : L.F("FileOperation.ErrorsFirst", context.Errors.Count.ToString(CultureInfo.InvariantCulture), context.Errors[0]);
    }

    private void Finish(OperationRun run, OperationState state, string? message)
    {
        lock (_gate)
        {
            run.State = state;
            run.Message = message;
            run.CompletedUtc = _clock.UtcNow;
            run.CurrentPath = null;
            run.PendingConflict = null;
            run.Decision = null;
            run.Clock.Stop();
        }

        PublishProgress(run, force: true);
        _events.Publish(new OperationCompletedEvent(run.Id, state switch
        {
            OperationState.Completed => OperationOutcome.Completed,
            OperationState.Cancelled => OperationOutcome.Cancelled,
            _ => OperationOutcome.Failed,
        }, message));
        LogFinished(_logger, run.Id, state, message);
    }

    private async Task CopyAllAsync(RunContext context, bool move, CancellationToken token)
    {
        var run = context.Run;
        var destination = run.Request.Destination!;
        SetTotals(run, run.Request.Sources.Select(Measure).Aggregate((0L, 0L), static (sum, item) => (sum.Item1 + item.Items, sum.Item2 + item.Bytes)));
        context.Affected.Add(destination);
        foreach (var source in run.Request.Sources)
        {
            token.ThrowIfCancellationRequested();
            if (move)
            {
                context.Affected.Add(_paths.GetParent(source) ?? source);
            }

            await CopyItemAsync(context, source, _paths.Combine(destination, _paths.GetFileName(source)), move, record: move, token).ConfigureAwait(false);
        }
    }

    // record — запомнить перенос для отмены: новая папка запоминается целиком, при слиянии с существующей — каждый элемент отдельно.
    private async Task CopyItemAsync(RunContext context, string source, string target, bool move, bool record, CancellationToken token)
    {
        try
        {
            if (_fs.Directory.Exists(source))
            {
                await CopyDirectoryAsync(context, source, target, move, record, token).ConfigureAwait(false);
            }
            else
            {
                await CopyFileAsync(context, source, target, move, record, token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            context.Errors.Add($"«{source}»: {exception.Message}");
        }
    }

    private async Task CopyFileAsync(RunContext context, string source, string target, bool move, bool record, CancellationToken token)
    {
        var run = context.Run;
        var size = _fs.FileInfo.New(source).Length;
        var resolved = await ResolveConflictAsync(context, source, target, token).ConfigureAwait(false);
        if (resolved is null)
        {
            AddProgress(run, items: 1, bytes: size, path: null);
            context.Skipped++;
            return;
        }

        SetCurrent(run, source);
        await CopyContentAsync(run, source, resolved, token).ConfigureAwait(false);
        AddProgress(run, items: 1, bytes: 0, path: null);
        if (move)
        {
            if (_fs.FileInfo.New(resolved).Length != size)
            {
                throw new IOException(L.F("FileOperation.TheCopyOfDidNotMatch", resolved));
            }

            ClearReadOnly(source);
            _fs.File.Delete(source);
            if (record)
            {
                run.Moves.Add((source, resolved));
            }
        }
    }

    private async Task CopyDirectoryAsync(RunContext context, string source, string target, bool move, bool record, CancellationToken token)
    {
        var run = context.Run;
        var resolved = await ResolveConflictAsync(context, source, target, token).ConfigureAwait(false);
        if (resolved is null)
        {
            var (items, bytes) = Measure(source);
            AddProgress(run, items, bytes, path: null);
            context.Skipped++;
            return;
        }

        var info = _fs.DirectoryInfo.New(source);
        var fresh = !_fs.Directory.Exists(resolved);
        _fs.Directory.CreateDirectory(resolved);
        AddProgress(run, items: 1, bytes: 0, path: source);
        foreach (var child in info.EnumerateFileSystemInfos())
        {
            token.ThrowIfCancellationRequested();
            if (child is IDirectoryInfo && (child.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                // Ссылки на другие папки не разворачиваются: это защищает от циклов и копирования чужих деревьев.
                context.Skipped++;
                continue;
            }

            await CopyItemAsync(context, child.FullName, _paths.Combine(resolved, child.Name), move, record && !fresh, token).ConfigureAwait(false);
        }

        if (fresh)
        {
            _fs.Directory.SetCreationTimeUtc(resolved, info.CreationTimeUtc);
            _fs.Directory.SetLastWriteTimeUtc(resolved, info.LastWriteTimeUtc);
        }

        if (move && !_fs.Directory.EnumerateFileSystemEntries(source).Any())
        {
            _fs.Directory.Delete(source, recursive: false);
            if (record && fresh)
            {
                run.Moves.Add((source, resolved));
            }
        }
    }

    private async Task CopyContentAsync(OperationRun run, string source, string target, CancellationToken token)
    {
        var created = false;
        var buffer = ArrayPool<byte>.Shared.Rent(_options.BufferSize);
        try
        {
            var input = _fs.FileStream.New(source, FileMode.Open, FileAccess.Read, FileShare.Read, _options.BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);
            await using (input.ConfigureAwait(false))
            {
                var output = _fs.FileStream.New(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, _options.BufferSize, FileOptions.Asynchronous);
                created = true;
                await using (output.ConfigureAwait(false))
                {
                    int read;
                    while ((read = await input.ReadAsync(buffer.AsMemory(0, _options.BufferSize), token).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                        AddProgress(run, items: 0, bytes: read, path: source);
                    }
                }
            }

            var info = _fs.FileInfo.New(source);
            _fs.File.SetCreationTimeUtc(target, info.CreationTimeUtc);
            _fs.File.SetLastWriteTimeUtc(target, info.LastWriteTimeUtc);
            _fs.File.SetAttributes(target, info.Attributes);
        }
        catch when (created)
        {
            // Недописанный файл создан этой операцией — его можно удалить безвозвратно.
            TryDeletePartial(target);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task MoveAllAsync(RunContext context, CancellationToken token)
    {
        var run = context.Run;
        var destination = run.Request.Destination!;
        if (!run.Request.Sources.All(source => SameVolume(source, destination)))
        {
            await CopyAllAsync(context, move: true, token).ConfigureAwait(false);
            return;
        }

        SetTotals(run, (run.Request.Sources.Count, 0));
        context.Affected.Add(destination);
        foreach (var source in run.Request.Sources)
        {
            token.ThrowIfCancellationRequested();
            context.Affected.Add(_paths.GetParent(source) ?? source);
            var target = _paths.Combine(destination, _paths.GetFileName(source));
            if (_paths.Comparer.Equals(source, target))
            {
                AddProgress(run, items: 1, bytes: 0, path: null);
                continue;
            }

            try
            {
                await MoveWithinVolumeAsync(context, source, target, token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                context.Errors.Add($"«{source}»: {exception.Message}");
            }

            AddProgress(run, items: 1, bytes: 0, path: null);
        }
    }

    private async Task MoveWithinVolumeAsync(RunContext context, string source, string target, CancellationToken token)
    {
        var isDirectory = _fs.Directory.Exists(source);
        var targetIsDirectory = _fs.Directory.Exists(target);
        var resolved = await ResolveConflictAsync(context, source, target, token).ConfigureAwait(false);
        if (resolved is null)
        {
            context.Skipped++;
            return;
        }

        SetCurrent(context.Run, source);
        if (isDirectory && targetIsDirectory && _paths.Comparer.Equals(resolved, target))
        {
            // Слияние папок: содержимое переносится по одному элементу, пустая папка-источник удаляется.
            foreach (var child in _fs.Directory.EnumerateFileSystemEntries(source).ToArray())
            {
                await MoveWithinVolumeAsync(context, child, _paths.Combine(target, _paths.GetFileName(child)), token).ConfigureAwait(false);
            }

            if (!_fs.Directory.EnumerateFileSystemEntries(source).Any())
            {
                _fs.Directory.Delete(source, recursive: false);
            }

            return;
        }

        if (isDirectory)
        {
            _fs.Directory.Move(source, resolved);
        }
        else
        {
            _fs.File.Move(source, resolved);
        }

        context.Run.Moves.Add((source, resolved));
    }

    private async Task MovePairsAsync(RunContext context, IReadOnlyList<(string From, string To)> pairs, CancellationToken token)
    {
        var run = context.Run;
        SetTotals(run, (pairs.Count, 0));
        foreach (var (from, to) in pairs.Reverse())
        {
            token.ThrowIfCancellationRequested();
            context.Affected.Add(_paths.GetParent(from) ?? from);
            context.Affected.Add(_paths.GetParent(to) ?? to);
            try
            {
                if (!_fs.File.Exists(from) && !_fs.Directory.Exists(from))
                {
                    throw new IOException(L.F("FileOperation.IsGone", from));
                }

                if (_fs.File.Exists(to) || (_fs.Directory.Exists(to) && !_paths.Comparer.Equals(from, to)))
                {
                    throw new IOException(L.F("FileOperation.SomethingAlreadyExistsInPlaceOf", to));
                }

                _fs.Directory.CreateDirectory(_paths.GetParent(to)!);
                if (SameVolume(from, to))
                {
                    MoveEntry(from, to);
                }
                else if (_fs.Directory.Exists(from))
                {
                    await CopyDirectoryAsync(context, from, to, move: true, record: false, token).ConfigureAwait(false);
                }
                else
                {
                    await CopyFileAsync(context, from, to, move: true, record: false, token).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                context.Errors.Add(L.F("FileOperation.CouldNotRestore", from, exception.Message));
            }

            AddProgress(run, items: 1, bytes: 0, path: null);
        }
    }

    private async Task RenameAsync(RunContext context, CancellationToken token)
    {
        var run = context.Run;
        var source = run.Request.Sources[0];
        var parent = _paths.GetParent(source)!;
        var target = _paths.Combine(parent, run.Request.NewName!);
        SetTotals(run, (1, 0));
        context.Affected.Add(parent);
        if (string.Equals(source, target, StringComparison.Ordinal))
        {
            AddProgress(run, items: 1, bytes: 0, path: null);
            return;
        }

        // Смена только регистра — не конфликт: на месте того же файла.
        var resolved = _paths.Comparer.Equals(source, target) ? target : await ResolveConflictAsync(context, source, target, token).ConfigureAwait(false);
        if (resolved is null)
        {
            context.Skipped++;
            AddProgress(run, items: 1, bytes: 0, path: null);
            return;
        }

        MoveEntry(source, resolved);
        run.Moves.Add((source, resolved));
        AddProgress(run, items: 1, bytes: 0, path: null);
    }

    private async Task DeleteAsync(RunContext context, CancellationToken token)
    {
        var run = context.Run;
        SetTotals(run, (run.Request.Sources.Count, 0));
        foreach (var source in run.Request.Sources)
        {
            context.Affected.Add(_paths.GetParent(source) ?? source);
        }

        if (!run.Request.Permanent)
        {
            await RecycleAsync(run.Request.Sources, token).ConfigureAwait(false);
            AddProgress(run, items: run.Request.Sources.Count, bytes: 0, path: null);
            return;
        }

        foreach (var source in run.Request.Sources)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                SetCurrent(run, source);
                DeletePermanently(source);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                context.Errors.Add($"«{source}»: {exception.Message}");
            }

            AddProgress(run, items: 1, bytes: 0, path: null);
        }
    }

    private Task CreateAsync(RunContext context, CancellationToken token)
    {
        var run = context.Run;
        var destination = run.Request.Destination!;
        var target = _paths.Combine(destination, run.Request.NewName!);
        SetTotals(run, (1, 0));
        context.Affected.Add(destination);
        if (_fs.File.Exists(target) || _fs.Directory.Exists(target))
        {
            switch (run.Request.Conflicts)
            {
                case ConflictPolicy.Skip:
                    context.Skipped++;
                    AddProgress(run, items: 1, bytes: 0, path: null);
                    return Task.CompletedTask;
                case ConflictPolicy.Overwrite:
                    throw new IOException(L.F("FileOperation.AlreadyExists", target));
                default:
                    target = UniqueName(target);
                    break;
            }
        }

        token.ThrowIfCancellationRequested();
        if (run.Request.Kind == FileOperationKind.CreateFolder)
        {
            _fs.Directory.CreateDirectory(target);
        }
        else
        {
            _fs.FileStream.New(target, FileMode.CreateNew, FileAccess.Write).Dispose();
        }

        run.Created = target;
        AddProgress(run, items: 1, bytes: 0, path: null);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Куда писать элемент при возможном конфликте; <c>null</c> — пропустить.
    /// </summary>
    private async Task<string?> ResolveConflictAsync(RunContext context, string source, string target, CancellationToken token)
    {
        var run = context.Run;
        var targetIsDirectory = _fs.Directory.Exists(target);
        if (!targetIsDirectory && !_fs.File.Exists(target))
        {
            return target;
        }

        if (_paths.Comparer.Equals(source, target))
        {
            // Копия в ту же папку — «имя (2)»; перенос на то же место — ничего делать не нужно.
            return run.Request.Kind == FileOperationKind.Copy ? UniqueName(target) : null;
        }

        var resolution = run.Request.Conflicts switch
        {
            ConflictPolicy.Skip => ConflictResolution.Skip,
            ConflictPolicy.Overwrite => ConflictResolution.Overwrite,
            ConflictPolicy.KeepBoth => ConflictResolution.KeepBoth,
            _ => run.ApplyToAll ?? await AskAsync(run, source, target, token).ConfigureAwait(false),
        };

        switch (resolution)
        {
            case ConflictResolution.Skip:
                return null;
            case ConflictResolution.KeepBoth:
                return UniqueName(target);
            case ConflictResolution.Overwrite:
                if (targetIsDirectory && _fs.Directory.Exists(source))
                {
                    return target;
                }

                await RecycleAsync([target], token).ConfigureAwait(false);
                return target;
            default:
                await run.Cancellation.CancelAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return null;
        }
    }

    private async Task<ConflictResolution> AskAsync(OperationRun run, string source, string target, CancellationToken token)
    {
        var sourceInfo = Describe(source);
        var targetInfo = Describe(target);
        var conflict = new OperationConflict(run.Id, source, target, sourceInfo.Size, sourceInfo.Modified, targetInfo.Size, targetInfo.Modified);
        var decision = new TaskCompletionSource<(ConflictResolution Resolution, bool ApplyToAll)>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            run.State = OperationState.WaitingDecision;
            run.PendingConflict = conflict;
            run.Decision = decision;
            run.Clock.Stop();
        }

        _events.Publish(new OperationNeedsDecisionEvent(run.Id, source, target, conflict.SourceSize, conflict.SourceModifiedUtc, conflict.TargetSize, conflict.TargetModifiedUtc));
        (ConflictResolution Resolution, bool ApplyToAll) answer;
        try
        {
            answer = await decision.Task.WaitAsync(token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                run.PendingConflict = null;
                run.Decision = null;
                if (run.State == OperationState.WaitingDecision)
                {
                    run.State = OperationState.Running;
                    run.Clock.Start();
                }
            }
        }

        if (answer.ApplyToAll && answer.Resolution != ConflictResolution.Cancel)
        {
            run.ApplyToAll = answer.Resolution;
        }

        return answer.Resolution;
    }

    private (long Size, DateTimeOffset Modified) Describe(string path)
    {
        if (_fs.Directory.Exists(path))
        {
            return (0, new DateTimeOffset(_fs.Directory.GetLastWriteTimeUtc(path), TimeSpan.Zero));
        }

        var info = _fs.FileInfo.New(path);
        return (info.Length, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
    }

    private string UniqueName(string target)
    {
        var parent = _paths.GetParent(target)!;
        var name = _paths.GetFileName(target);
        var isDirectory = _fs.Directory.Exists(target);
        var dot = isDirectory ? -1 : name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var extension = dot > 0 ? name[dot..] : string.Empty;
        for (var i = 2; ; i++)
        {
            var candidate = _paths.Combine(parent, $"{stem} ({i.ToString(CultureInfo.InvariantCulture)}){extension}");
            if (!_fs.File.Exists(candidate) && !_fs.Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private async Task RecycleAsync(IReadOnlyList<string> paths, CancellationToken token)
    {
        if (_shell is null)
        {
            throw new IOException(L.T("FileOperation.TheRecycleBinIsNotAvailable"));
        }

        var result = await _shell.MoveToRecycleBinAsync(paths, token).ConfigureAwait(false);
        if (result.IsFailure)
        {
            throw new IOException(result.Error!.Message);
        }
    }

    private void MoveEntry(string source, string target)
    {
        if (_fs.Directory.Exists(source))
        {
            if (_paths.Comparer.Equals(source, target))
            {
                // Windows не переименовывает папку со сменой только регистра напрямую — через временное имя.
                var temporary = source + ".diskapp-" + Guid.NewGuid().ToString("N")[..8];
                _fs.Directory.Move(source, temporary);
                _fs.Directory.Move(temporary, target);
            }
            else
            {
                _fs.Directory.Move(source, target);
            }
        }
        else
        {
            _fs.File.Move(source, target);
        }
    }

    private void DeletePermanently(string path)
    {
        if (_fs.Directory.Exists(path))
        {
            foreach (var file in _fs.Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                ClearReadOnly(file);
            }

            _fs.Directory.Delete(path, recursive: true);
        }
        else
        {
            ClearReadOnly(path);
            _fs.File.Delete(path);
        }
    }

    private void ClearReadOnly(string file)
    {
        var attributes = _fs.File.GetAttributes(file);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            _fs.File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private void TryDeletePartial(string target)
    {
        try
        {
            if (_fs.File.Exists(target))
            {
                ClearReadOnly(target);
                _fs.File.Delete(target);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogPartialNotDeleted(_logger, exception, target);
        }
    }

    private (long Items, long Bytes) Measure(string path)
    {
        if (!_fs.Directory.Exists(path))
        {
            return (1, _fs.File.Exists(path) ? _fs.FileInfo.New(path).Length : 0);
        }

        long items = 1;
        long bytes = 0;
        var pending = new Stack<IDirectoryInfo>();
        pending.Push(_fs.DirectoryInfo.New(path));
        while (pending.Count > 0)
        {
            foreach (var child in pending.Pop().EnumerateFileSystemInfos())
            {
                items++;
                if (child is IFileInfo file)
                {
                    bytes += file.Length;
                }
                else if (child is IDirectoryInfo directory && (directory.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    pending.Push(directory);
                }
            }
        }

        return (items, bytes);
    }

    private bool SameVolume(string left, string right)
        => _options.SameVolume?.Invoke(left, right) ?? _paths.Comparer.Equals(_paths.GetRoot(left), _paths.GetRoot(right));

    private void SetTotals(OperationRun run, (long Items, long Bytes) totals)
    {
        lock (_gate)
        {
            run.TotalItems = totals.Items;
            run.TotalBytes = totals.Bytes;
        }

        PublishProgress(run, force: true);
    }

    private void SetCurrent(OperationRun run, string path)
    {
        lock (_gate)
        {
            run.CurrentPath = path;
        }

        PublishProgress(run, force: false);
    }

    private void AddProgress(OperationRun run, long items, long bytes, string? path)
    {
        lock (_gate)
        {
            run.ProcessedItems += items;
            run.ProcessedBytes += bytes;
            if (path is not null)
            {
                run.CurrentPath = path;
            }
        }

        PublishProgress(run, force: false);
    }

    private void PublishProgress(OperationRun run, bool force)
    {
        FileOperationInfo snapshot;
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            if (!force && Stopwatch.GetElapsedTime(run.LastPublished, now) < _options.ProgressInterval)
            {
                return;
            }

            run.LastPublished = now;
            snapshot = Snapshot(run);
        }

        var progress = snapshot.Progress;
        _events.Publish(new OperationProgressEvent(run.Id, progress.ProcessedItems, progress.TotalItems, progress.ProcessedBytes, progress.TotalBytes,
            progress.CurrentPath, progress.BytesPerSecond, progress.Remaining));
    }

    [LoggerMessage(EventId = 4101, Level = LogLevel.Information, Message = "Операция {Id} ({Kind}, элементов: {Count}) запущена")]
    private static partial void LogStarted(ILogger logger, Guid id, FileOperationKind kind, int count);

    [LoggerMessage(EventId = 4102, Level = LogLevel.Information, Message = "Операция {Id} завершена: {State}. {Message}")]
    private static partial void LogFinished(ILogger logger, Guid id, OperationState state, string? message);

    [LoggerMessage(EventId = 4103, Level = LogLevel.Warning, Message = "Не удалось удалить недописанный файл {Path}")]
    private static partial void LogPartialNotDeleted(ILogger logger, Exception exception, string path);

    private sealed record RunContext(OperationRun Run, HashSet<string> Affected, List<string> Errors)
    {
        public int Skipped { get; set; }
    }

    private sealed class OperationRun(Guid id, FileOperationRequest request, DateTimeOffset createdUtc, CancellationToken stop)
    {
        public Guid Id { get; } = id;

        public FileOperationRequest Request { get; } = request;

        public DateTimeOffset CreatedUtc { get; } = createdUtc;

        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(stop);

        public Stopwatch Clock { get; } = new();

        public OperationState State { get; set; } = OperationState.Queued;

        public DateTimeOffset? CompletedUtc { get; set; }

        public string? Message { get; set; }

        public long ProcessedItems { get; set; }

        public long TotalItems { get; set; }

        public long ProcessedBytes { get; set; }

        public long TotalBytes { get; set; }

        public string? CurrentPath { get; set; }

        public long LastPublished { get; set; }

        public OperationConflict? PendingConflict { get; set; }

        public TaskCompletionSource<(ConflictResolution Resolution, bool ApplyToAll)>? Decision { get; set; }

        public ConflictResolution? ApplyToAll { get; set; }

        public List<(string From, string To)> Moves { get; } = [];

        public string? Created { get; set; }

        public bool Undone { get; set; }

        public bool IsUndo { get; init; }

        public IReadOnlyList<(string From, string To)>? UndoMoves { get; init; }
    }
}

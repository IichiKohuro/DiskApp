using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

using DiskApp.Core.Domain;
using DiskApp.Core.FileSystem;

namespace DiskApp.Core.Indexing;

/// <summary>
/// Обход папок для индекса (PLAN.md, этап 2.1). Итеративный, на собственном стеке; начальная папка читается первой,
/// её подпапки делятся между потоками (степень параллелизма — по типу диска). Каждый поток обходит свои папки целиком
/// и складывает записи в свои пакеты, поэтому родитель всегда попадает в канал раньше детей.
/// Junction и символические ссылки по умолчанию попадают в индекс, но внутрь них обход не заходит;
/// при включённом переходе по ссылкам циклы отсекаются по идентичности папки (том и номер файла).
/// Недоступные папки записываются в итог и не прерывают обход. Потоки выделенные, с фоновым приоритетом.
/// </summary>
public sealed class DirectoryScanner(IFileSystemLister lister, EntryFactory entries, IBackgroundPriority? priority = null)
{
    private readonly IBackgroundPriority _priority = priority ?? NoBackgroundPriority.Instance;

    /// <summary>
    /// Обходит корень или поддерево и пишет пакеты в <paramref name="output"/>. Канал не закрывается: это делает вызывающий.
    /// </summary>
    /// <param name="request">Что обходить.</param>
    /// <param name="output">Куда писать пакеты; ограниченный канал даёт противодавление.</param>
    /// <param name="counters">Счётчики для отчёта о прогрессе.</param>
    /// <param name="pause">Пауза: проверяется перед каждой папкой.</param>
    /// <param name="cancellationToken">Отмена.</param>
    public Task<ScanSummary> ScanAsync(
        ScanRequest request,
        ChannelWriter<EntryBatch> output,
        ScanCounters counters,
        PauseToken pause,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.BatchSize, 1);

        return Task.Factory.StartNew(
            () => Scan(request, output, counters, pause, cancellationToken),
            cancellationToken,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    private ScanSummary Scan(ScanRequest request, ChannelWriter<EntryBatch> output, ScanCounters counters, PauseToken pause, CancellationToken cancellationToken)
    {
        using var background = _priority.Enter();
        var stopwatch = Stopwatch.StartNew();
        var root = request.Root;
        var startRelPath = entries.Paths.NormalizeRelative(request.RelPath);
        var startFullPath = entries.FullPath(root, startRelPath);
        var startDepth = entries.Depth(startRelPath);
        var state = new ScanState(request, output, counters, pause, new DescentGuard(lister, entries.Paths, request.FollowReparsePoints));

        if (startRelPath.Length > 0 && request.Matcher.IsPathExcluded(startRelPath))
        {
            return new ScanSummary { StartExcluded = true, Elapsed = stopwatch.Elapsed };
        }

        var startBuffer = new List<FileEntry>();
        if (startRelPath.Length == 0)
        {
            if (!lister.DirectoryExists(startFullPath))
            {
                return new ScanSummary { StartMissing = true, Elapsed = stopwatch.Elapsed };
            }
        }
        else
        {
            var stat = lister.Stat(startFullPath);
            if (stat.Error is not null)
            {
                // Папка есть, но недоступна: её записи в индексе сохраняются как есть.
                state.AddFailure(startRelPath, stat.Error);
                return new ScanSummary
                {
                    Errors = state.SnapshotErrors(),
                    ErrorCount = 1,
                    FailedDirectories = [startRelPath],
                    Elapsed = stopwatch.Elapsed,
                };
            }

            if (!stat.Exists || !stat.Item!.Value.IsDirectory)
            {
                // Нет папки или на её месте файл: поддерево обходить нечего, решение принимает вызывающий.
                return new ScanSummary { StartMissing = true, Elapsed = stopwatch.Elapsed };
            }

            var item = stat.Item.Value;
            if (request.Matcher.IsExcluded(item.Name, startRelPath, true, item.Attributes, 0, startDepth))
            {
                return new ScanSummary { StartExcluded = true, Elapsed = stopwatch.Elapsed };
            }

            startBuffer.Add(entries.Create(root.Id, startRelPath, item));
            counters.AddEntries(1);
        }

        state.Descent.RegisterStart(startFullPath);

        // Начальная папка читается здесь, её подпапки уходят в общую очередь потоков; пакет начальной папки
        // пишется до запуска потоков, чтобы папки верхнего уровня оказались в индексе раньше своих детей.
        var topLevel = new ConcurrentQueue<(string RelPath, int Depth)>();
        ProcessDirectory(state, startRelPath, startDepth, startBuffer, child => topLevel.Enqueue(child), cancellationToken);
        Flush(state, ref startBuffer, cancellationToken);

        var workers = Math.Clamp(request.DegreeOfParallelism, 1, Math.Max(1, topLevel.Count));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = new Task[workers - 1];
        for (var i = 0; i < tasks.Length; i++)
        {
            tasks[i] = Task.Factory.StartNew(
                () =>
                {
                    using var workerBackground = _priority.Enter();
                    RunWorker(state, topLevel, linked);
                },
                linked.Token,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);
        }

        try
        {
            RunWorker(state, topLevel, linked);
        }
        finally
        {
            try
            {
                Task.WaitAll(tasks, CancellationToken.None);
            }
            catch (AggregateException) when (linked.IsCancellationRequested || tasks.Any(static task => task.IsFaulted))
            {
                // Ошибка одного потока остановила остальные; ниже пробрасывается первопричина.
            }
        }

        var firstFault = tasks.FirstOrDefault(static task => task.IsFaulted)?.Exception?.InnerException;
        if (firstFault is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstFault).Throw();
        }

        cancellationToken.ThrowIfCancellationRequested();

        return new ScanSummary
        {
            Directories = counters.Directories,
            Entries = counters.Entries,
            Errors = state.SnapshotErrors(),
            ErrorCount = counters.Errors,
            FailedDirectories = [.. state.FailedDirectories],
            Elapsed = stopwatch.Elapsed,
        };
    }

    private void RunWorker(ScanState state, ConcurrentQueue<(string RelPath, int Depth)> topLevel, CancellationTokenSource linked)
    {
        var token = linked.Token;
        var buffer = new List<FileEntry>(state.Request.BatchSize);
        var stack = new Stack<(string RelPath, int Depth)>();

        try
        {
            while (topLevel.TryDequeue(out var top))
            {
                stack.Push(top);
                while (stack.TryPop(out var directory))
                {
                    ProcessDirectory(state, directory.RelPath, directory.Depth, buffer, stack.Push, token);
                    if (buffer.Count >= state.Request.BatchSize)
                    {
                        Flush(state, ref buffer, token);
                    }
                }
            }

            Flush(state, ref buffer, token);
        }
        catch
        {
            linked.Cancel();
            throw;
        }
    }

    private void ProcessDirectory(
        ScanState state,
        string relPath,
        int depth,
        List<FileEntry> buffer,
        Action<(string RelPath, int Depth)> descend,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        state.Pause.WaitWhilePaused(cancellationToken);

        var request = state.Request;
        var fullPath = entries.FullPath(request.Root, relPath);
        state.Counters.SetCurrentPath(fullPath);

        var listing = lister.List(fullPath);
        state.Counters.AddDirectory();
        if (!listing.Succeeded)
        {
            state.AddFailure(relPath, listing.Error!);
        }

        var added = 0;
        var childDepth = depth + 1;
        foreach (var listed in listing.Items)
        {
            var childRelPath = entries.Child(relPath, listed.Name);
            if (request.Matcher.IsExcluded(listed.Name, childRelPath, listed.IsDirectory, listed.Attributes, listed.Size, childDepth))
            {
                continue;
            }

            var item = EntryFactory.RefreshDirectory(lister, entries.Paths.Combine(fullPath, listed.Name), listed);

            buffer.Add(entries.Create(request.Root.Id, childRelPath, item));
            added++;

            if (item.IsDirectory && request.Matcher.CanDescend(childDepth) && state.Descent.ShouldDescend(fullPath, item))
            {
                descend((childRelPath, childDepth));
            }
        }

        state.Counters.AddEntries(added);
    }

    private static void Flush(ScanState state, ref List<FileEntry> buffer, CancellationToken cancellationToken)
    {
        if (buffer.Count == 0)
        {
            return;
        }

        var batch = new EntryBatch(buffer);
        buffer = new List<FileEntry>(state.Request.BatchSize);

        var writer = state.Output;
        while (!writer.TryWrite(batch))
        {
            if (!writer.WaitToWriteAsync(cancellationToken).AsTask().GetAwaiter().GetResult())
            {
                throw new ChannelClosedException("Канал пакетов закрыт до окончания обхода.");
            }
        }
    }

    private sealed class ScanState(ScanRequest request, ChannelWriter<EntryBatch> output, ScanCounters counters, PauseToken pause, DescentGuard descent)
    {
        private readonly Lock _errorsGate = new();
        private readonly List<ScanError> _errors = [];

        public ScanRequest Request { get; } = request;

        public ChannelWriter<EntryBatch> Output { get; } = output;

        public ScanCounters Counters { get; } = counters;

        public PauseToken Pause { get; } = pause;

        public DescentGuard Descent { get; } = descent;

        public ConcurrentBag<string> FailedDirectories { get; } = [];

        public void AddFailure(string relPath, string message)
        {
            FailedDirectories.Add(relPath);
            Counters.AddError();
            lock (_errorsGate)
            {
                if (_errors.Count < ScanSummary.MaxStoredErrors)
                {
                    _errors.Add(new ScanError(relPath, message));
                }
            }
        }

        public ScanError[] SnapshotErrors()
        {
            lock (_errorsGate)
            {
                return [.. _errors];
            }
        }
    }
}

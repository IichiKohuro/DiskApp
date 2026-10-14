using System.Diagnostics;

using DiskApp.Core.Abstractions;
using DiskApp.Core.Domain;
using DiskApp.Core.Events;
using DiskApp.Core.FileSystem;
using DiskApp.Core.Repositories;

namespace DiskApp.Core.Indexing;

/// <summary>
/// Сверка индекса с диском по разнице (PLAN.md, этап 2.4): обходит папки, сравнивает содержимое каждой с её детьми
/// в индексе и пишет только отличия — новые и изменённые записи, удаление исчезнувших, переименование при смене
/// регистра. В отличие от полного сканирования, не трогает неизменённые строки, поэтому почти не пишет в WAL.
/// Ловит и изменения файлов (размер, время), и изменения папок, сделанные, пока приложение не работало. Структурные
/// отличия — появления и исчезновения верхнего уровня, смена регистра имени — уходят в историю файлов
/// (<see cref="IndexJournalEvent"/>, этап 20), если сверка не служебная (<see cref="ScanRequest.Journal"/>).
/// </summary>
public sealed class ReconcileRunner(
    IFileSystemLister lister,
    IFileIndexRepository entries,
    IScanLogRepository scanLog,
    IIndexEventBus events,
    IClock clock,
    EntryFactory factory)
{
    /// <summary>
    /// Сверяет корень или поддерево.
    /// </summary>
    public async Task<ScanOutcome> RunAsync(ScanRequest request, PauseToken pause, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();
        var root = request.Root;
        var rootId = root.Id;
        var paths = factory.Paths;
        var startRelPath = paths.NormalizeRelative(request.RelPath);
        var scanId = await entries.NextScanIdAsync(rootId, cancellationToken).ConfigureAwait(false);
        var state = new ReconcileState(request, scanId, new ProgressReporter(events, rootId, ProgressReporter.DefaultInterval), new FolderChangeSet(paths));

        state.Progress.Report(IndexPhase.Enumerating, 0, 0, root.Path);

        var startOutcome = await PrepareStartAsync(state, startRelPath, cancellationToken).ConfigureAwait(false);
        if (startOutcome is not null)
        {
            state.Changes.PublishTo(events, rootId);
            PublishJournal(state);
            state.Progress.Report(IndexPhase.Done, 0, 0, null);
            return startOutcome with { Elapsed = stopwatch.Elapsed };
        }

        var guard = new DescentGuard(lister, paths, request.FollowReparsePoints);
        guard.RegisterStart(factory.FullPath(root, startRelPath));
        var stack = new Stack<(string RelPath, int Depth)>();
        stack.Push((startRelPath, factory.Depth(startRelPath)));

        while (stack.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await pause.WaitWhilePausedAsync(cancellationToken).ConfigureAwait(false);
            await ReconcileDirectoryAsync(state, guard, directory.RelPath, directory.Depth, stack, cancellationToken).ConfigureAwait(false);

            if (state.Pending.Count >= request.BatchSize)
            {
                await FlushAsync(state, cancellationToken).ConfigureAwait(false);
            }

            state.Progress.Report(IndexPhase.Enumerating, state.Found, state.Written.Total + state.Removed, factory.FullPath(root, directory.RelPath));
        }

        await FlushAsync(state, cancellationToken).ConfigureAwait(false);
        state.Changes.PublishTo(events, rootId);
        PublishJournal(state);
        await WriteLogAsync(state, cancellationToken).ConfigureAwait(false);
        state.Progress.Report(IndexPhase.Done, state.Found, state.Written.Total + state.Removed, null);

        return new ScanOutcome
        {
            Kind = IndexTaskKind.Reconcile,
            RootId = rootId,
            RelPath = startRelPath,
            Status = ScanOutcomeStatus.Completed,
            ScanId = scanId,
            Written = state.Written,
            Removed = state.Removed,
            Found = state.Found,
            ErrorCount = state.Errors.Count,
            Elapsed = stopwatch.Elapsed,
        };
    }

    /// <summary>
    /// Проверяет начальную папку. Возвращает итог, если сверять нечего (корня нет, поддерево исчезло или исключено),
    /// иначе <c>null</c> — сверка продолжается; при необходимости запись самой папки ставится в очередь.
    /// </summary>
    private async Task<ScanOutcome?> PrepareStartAsync(ReconcileState state, string startRelPath, CancellationToken cancellationToken)
    {
        var root = state.Request.Root;
        if (startRelPath.Length == 0)
        {
            return lister.DirectoryExists(root.Path)
                ? null
                : Outcome(state, startRelPath, ScanOutcomeStatus.RootMissing);
        }

        if (state.Request.Matcher.IsPathExcluded(startRelPath))
        {
            await RemoveAsync(state, startRelPath, cancellationToken).ConfigureAwait(false);
            return Outcome(state, startRelPath, ScanOutcomeStatus.Excluded);
        }

        var stat = lister.Stat(factory.FullPath(root, startRelPath));
        if (stat.Error is not null)
        {
            state.Errors.Add(new ScanError(startRelPath, stat.Error));
            return Outcome(state, startRelPath, ScanOutcomeStatus.Completed);
        }

        if (!stat.Exists || !stat.Item!.Value.IsDirectory)
        {
            await RemoveAsync(state, startRelPath, cancellationToken).ConfigureAwait(false);
            return Outcome(state, startRelPath, ScanOutcomeStatus.SubtreeMissing);
        }

        var item = stat.Item.Value;
        var indexed = await entries.GetByPathAsync(root.Id, startRelPath, cancellationToken).ConfigureAwait(false);
        if (indexed is null || EntryFactory.HasChanged(indexed, item))
        {
            state.Pending.Add(factory.Create(root.Id, startRelPath, item));
            state.Changes.Add(factory.Parent(startRelPath));
            if (indexed is null)
            {
                RecordAdded(state, startRelPath, item);
            }
        }

        return null;
    }

    private async Task ReconcileDirectoryAsync(
        ReconcileState state,
        DescentGuard guard,
        string relPath,
        int depth,
        Stack<(string RelPath, int Depth)> stack,
        CancellationToken cancellationToken)
    {
        var request = state.Request;
        var rootId = request.Root.Id;
        var fullPath = factory.FullPath(request.Root, relPath);
        var listing = lister.List(fullPath);
        if (!listing.Succeeded)
        {
            // Папку прочитать не удалось: её записи в индексе остаются как есть.
            state.Errors.Add(new ScanError(relPath, listing.Error!));
            return;
        }

        var indexed = await entries.GetChildrenOfPathAsync(rootId, relPath, cancellationToken).ConfigureAwait(false);
        var byName = new Dictionary<string, FileEntry>(indexed.Count, StringComparer.Ordinal);
        foreach (var entry in indexed)
        {
            byName[entry.Name] = entry;
        }

        var byNameIgnoreCase = factory.Paths.Comparer.Equals("A", "a")
            ? indexed.GroupBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase)
            : null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var childDepth = depth + 1;

        foreach (var listed in listing.Items)
        {
            var childRelPath = factory.Child(relPath, listed.Name);
            if (request.Matcher.IsExcluded(listed.Name, childRelPath, listed.IsDirectory, listed.Attributes, listed.Size, childDepth))
            {
                continue;
            }

            var item = EntryFactory.RefreshDirectory(lister, factory.Paths.Combine(fullPath, listed.Name), listed);

            state.Found++;
            if (byName.TryGetValue(item.Name, out var existing))
            {
                seen.Add(existing.Name);
                if (existing.IsDirectory != item.IsDirectory)
                {
                    await RemoveAsync(state, existing.RelPath, cancellationToken, existing).ConfigureAwait(false);
                    Queue(state, relPath, childRelPath, item);
                    RecordAdded(state, childRelPath, item);
                }
                else if (EntryFactory.HasChanged(existing, item))
                {
                    Queue(state, relPath, childRelPath, item);
                }
            }
            else if (byNameIgnoreCase is not null
                && byNameIgnoreCase.TryGetValue(item.Name, out var differentCase)
                && !seen.Contains(differentCase.Name)
                && differentCase.IsDirectory == item.IsDirectory)
            {
                // Смена регистра имени: переименование сохраняет идентификатор, статистику и избранное.
                seen.Add(differentCase.Name);
                await entries.RenameSubtreeAsync(rootId, differentCase.RelPath, childRelPath, cancellationToken).ConfigureAwait(false);
                state.Changes.Add(relPath);
                Queue(state, relPath, childRelPath, item);
                if (request.Journal)
                {
                    state.Journal.Add(new JournalChange(JournalChangeKind.Renamed, factory.FullPath(request.Root, childRelPath), factory.FullPath(request.Root, differentCase.RelPath), item.IsDirectory, differentCase.Size, differentCase.CreatedUtc, differentCase.ModifiedUtc));
                }
            }
            else
            {
                Queue(state, relPath, childRelPath, item);
                RecordAdded(state, childRelPath, item);
            }

            if (item.IsDirectory && request.Matcher.CanDescend(childDepth) && guard.ShouldDescend(fullPath, item))
            {
                stack.Push((childRelPath, childDepth));
            }
        }

        foreach (var entry in indexed)
        {
            if (!seen.Contains(entry.Name) && !state.IsQueued(entry.RelPath))
            {
                await RemoveAsync(state, entry.RelPath, cancellationToken, entry).ConfigureAwait(false);
            }
        }
    }

    private void Queue(ReconcileState state, string parentRelPath, string relPath, in FileSystemItem item)
    {
        state.Pending.Add(factory.Create(state.Request.Root.Id, relPath, item));
        state.QueuedPaths.Add(relPath);
        state.Changes.Add(parentRelPath);
    }

    private async Task RemoveAsync(ReconcileState state, string relPath, CancellationToken cancellationToken, FileEntry? entry = null)
    {
        // Перед удалением записываем накопленное: в очереди могут быть записи, которые должны оказаться раньше.
        await FlushAsync(state, cancellationToken).ConfigureAwait(false);
        if (state.Request.Journal)
        {
            entry ??= await entries.GetByPathAsync(state.Request.Root.Id, relPath, cancellationToken).ConfigureAwait(false);
        }

        var removed = await entries.DeleteSubtreeAsync(state.Request.Root.Id, relPath, cancellationToken).ConfigureAwait(false);
        if (removed > 0)
        {
            state.Removed += removed;
            state.Changes.Add(factory.Parent(relPath));
            if (state.Request.Journal && entry is not null)
            {
                state.Journal.Add(new JournalChange(JournalChangeKind.Removed, factory.FullPath(state.Request.Root, relPath), null, entry.IsDirectory, entry.Size, entry.CreatedUtc, entry.ModifiedUtc));
            }
        }
    }

    // В историю — только верхний появившийся элемент: содержимое новой папки — часть её появления.
    private void RecordAdded(ReconcileState state, string relPath, in FileSystemItem item)
    {
        if (!state.Request.Journal)
        {
            return;
        }

        var separator = factory.Paths.Separator;
        if (state.AddedDirectories.Exists(directory => relPath.StartsWith(directory + separator, factory.Paths.Comparison)))
        {
            return;
        }

        var entry = factory.Create(state.Request.Root.Id, relPath, item);
        state.Journal.Add(new JournalChange(JournalChangeKind.Added, factory.FullPath(state.Request.Root, relPath), null, entry.IsDirectory, entry.Size, entry.CreatedUtc, entry.ModifiedUtc));
        if (item.IsDirectory)
        {
            state.AddedDirectories.Add(relPath);
        }
    }

    private void PublishJournal(ReconcileState state)
    {
        if (state.Journal.Count > 0)
        {
            events.Publish(new IndexJournalEvent(state.Request.Root.Id, JournalSource.Reconcile, [.. state.Journal]));
        }
    }

    private async Task FlushAsync(ReconcileState state, CancellationToken cancellationToken)
    {
        if (state.Pending.Count == 0)
        {
            return;
        }

        var batch = state.Pending.ToArray();
        state.Pending.Clear();
        state.QueuedPaths.Clear();
        state.Written += await entries.UpsertAsync(batch, state.ScanId, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteLogAsync(ReconcileState state, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var log = state.Errors
            .Take(ScanSummary.MaxStoredErrors)
            .Select(error => new ScanLogEntry(state.Request.Root.Id, state.ScanId, now, ScanLogLevel.Warning, error.RelPath, error.Message))
            .ToList();
        await scanLog.AddAsync(log, cancellationToken).ConfigureAwait(false);
    }

    private static ScanOutcome Outcome(ReconcileState state, string relPath, ScanOutcomeStatus status) => new()
    {
        Kind = IndexTaskKind.Reconcile,
        RootId = state.Request.Root.Id,
        RelPath = relPath,
        Status = status,
        ScanId = state.ScanId,
        Written = state.Written,
        Removed = state.Removed,
        ErrorCount = state.Errors.Count,
    };

    private sealed class ReconcileState(ScanRequest request, long scanId, ProgressReporter progress, FolderChangeSet changes)
    {
        public ScanRequest Request { get; } = request;

        public long ScanId { get; } = scanId;

        public ProgressReporter Progress { get; } = progress;

        public FolderChangeSet Changes { get; } = changes;

        public List<FileEntry> Pending { get; } = [];

        public HashSet<string> QueuedPaths { get; } = new(StringComparer.Ordinal);

        public List<JournalChange> Journal { get; } = [];

        public List<string> AddedDirectories { get; } = [];

        public List<ScanError> Errors { get; } = [];

        public UpsertResult Written { get; set; }

        public int Removed { get; set; }

        public long Found { get; set; }

        public bool IsQueued(string relPath) => QueuedPaths.Contains(relPath);
    }
}

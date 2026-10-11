using System.Diagnostics;
using System.Threading.Channels;

using DiskApp.Core.Abstractions;
using DiskApp.Core.Events;
using DiskApp.Core.Localization;
using DiskApp.Core.Repositories;

namespace DiskApp.Core.Indexing;

/// <summary>
/// Полное сканирование как mark-and-sweep (PLAN.md, этап 2.2): сканер пишет пакеты в ограниченный канал
/// (противодавление), единственный потребитель записывает их в индекс с новым номером сканирования; неизменённые
/// строки только помечаются. В конце записи, не помеченные этим сканированием, удаляются — так находятся файлы,
/// удалённые, пока приложение не работало. Папки, которые не удалось прочитать, помечаются целиком и не теряются.
/// Если корня нет, индекс не трогается.
/// </summary>
public sealed class ScanPipeline(
    DirectoryScanner scanner,
    IFileIndexRepository entries,
    IScanLogRepository scanLog,
    IIndexEventBus events,
    IClock clock,
    EntryFactory factory)
{
    private const int ChannelCapacity = 4;

    /// <summary>
    /// Выполняет полное сканирование корня или поддерева.
    /// </summary>
    /// <param name="request">Что сканировать.</param>
    /// <param name="pause">Пауза.</param>
    /// <param name="onScanStarted">Вызывается с номером сканирования до записи первого пакета: наблюдатель помечает им свои записи.</param>
    /// <param name="cancellationToken">Отмена: прерванное сканирование ничего не удаляет.</param>
    public async Task<ScanOutcome> RunAsync(ScanRequest request, PauseToken pause, Action<long>? onScanStarted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stopwatch = Stopwatch.StartNew();
        var rootId = request.Root.Id;
        var relPath = factory.Paths.NormalizeRelative(request.RelPath);
        var scanId = await entries.NextScanIdAsync(rootId, cancellationToken).ConfigureAwait(false);
        onScanStarted?.Invoke(scanId);

        var progress = new ProgressReporter(events, rootId, ProgressReporter.DefaultInterval);
        var counters = new ScanCounters();
        var changes = new FolderChangeSet(factory.Paths);
        var channel = Channel.CreateBounded<EntryBatch>(new BoundedChannelOptions(ChannelCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        progress.Report(IndexPhase.Enumerating, 0, 0, request.Root.Path);

        var producer = scanner.ScanAsync(request with { RelPath = relPath }, channel.Writer, counters, pause, linked.Token);
        _ = producer.ContinueWith(
            task => channel.Writer.TryComplete(task.Exception?.InnerException),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        var written = default(UpsertResult);
        try
        {
            await foreach (var batch in channel.Reader.ReadAllAsync(linked.Token).ConfigureAwait(false))
            {
                var details = await entries.UpsertDetailedAsync(batch.Entries, scanId, linked.Token).ConfigureAwait(false);
                written += details.Totals;
                AddChangedFolders(changes, batch, details);
                changes.PublishTo(events, rootId);

                var phase = producer.IsCompleted ? IndexPhase.Writing : IndexPhase.Enumerating;
                progress.Report(phase, counters.Entries, written.Total, counters.CurrentPath);
            }
        }
        catch
        {
            await linked.CancelAsync().ConfigureAwait(false);
            channel.Writer.TryComplete();
            await SwallowAsync(producer).ConfigureAwait(false);
            throw;
        }

        var summary = await producer.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var status = ScanOutcomeStatus.Completed;
        var removed = 0;
        progress.Report(IndexPhase.Sweeping, counters.Entries, written.Total, null);

        if (summary.StartMissing && relPath.Length == 0)
        {
            status = ScanOutcomeStatus.RootMissing;
        }
        else if (summary.StartMissing || summary.StartExcluded)
        {
            status = summary.StartMissing ? ScanOutcomeStatus.SubtreeMissing : ScanOutcomeStatus.Excluded;
            removed = await entries.DeleteSubtreeAsync(rootId, relPath, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            foreach (var failed in summary.FailedDirectories)
            {
                await entries.MarkSubtreeAsync(rootId, failed, scanId, cancellationToken).ConfigureAwait(false);
            }

            removed = relPath.Length == 0
                ? await entries.SweepAsync(rootId, scanId, cancellationToken).ConfigureAwait(false)
                : await entries.SweepSubtreeAsync(rootId, relPath, scanId, cancellationToken).ConfigureAwait(false);
        }

        if (removed > 0)
        {
            changes.Add(relPath.Length == 0 ? string.Empty : factory.Parent(relPath));
            changes.Add(relPath);
            changes.PublishTo(events, rootId);
        }

        await WriteLogAsync(rootId, scanId, relPath, summary, status, cancellationToken).ConfigureAwait(false);
        progress.Report(IndexPhase.Done, counters.Entries, written.Total, null);

        return new ScanOutcome
        {
            Kind = IndexTaskKind.FullScan,
            RootId = rootId,
            RelPath = relPath,
            Status = status,
            ScanId = scanId,
            Written = written,
            Removed = removed,
            Found = summary.Entries,
            ErrorCount = summary.ErrorCount,
            Elapsed = stopwatch.Elapsed,
        };
    }

    private void AddChangedFolders(FolderChangeSet changes, EntryBatch batch, UpsertDetails details)
    {
        foreach (var index in details.InsertedIndices)
        {
            changes.Add(factory.Parent(batch.Entries[index].RelPath));
        }

        foreach (var index in details.UpdatedIndices)
        {
            changes.Add(factory.Parent(batch.Entries[index].RelPath));
        }
    }

    private async Task WriteLogAsync(long rootId, long scanId, string relPath, ScanSummary summary, ScanOutcomeStatus status, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var log = new List<ScanLogEntry>(summary.Errors.Count + 2);
        foreach (var error in summary.Errors)
        {
            log.Add(new ScanLogEntry(rootId, scanId, now, ScanLogLevel.Warning, error.RelPath, error.Message));
        }

        if (summary.ErrorCount > summary.Errors.Count)
        {
            log.Add(new ScanLogEntry(rootId, scanId, now, ScanLogLevel.Warning, null, L.F("ScanPipeline.MoreErrorsAreNotLoggedIn", summary.ErrorCount - summary.Errors.Count)));
        }

        if (status == ScanOutcomeStatus.RootMissing)
        {
            log.Add(new ScanLogEntry(rootId, scanId, now, ScanLogLevel.Error, null, L.T("ScanPipeline.TheRootIsUnavailableTheIndex")));
        }

        await scanLog.AddAsync(log, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SwallowAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Первопричину пробрасывает вызывающий; ошибка производителя после отмены вторична.
        }
    }
}

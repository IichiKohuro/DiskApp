using System.Threading.Channels;

using DiskApp.Core.Domain;
using DiskApp.Core.Indexing;
using DiskApp.Core.Paths;
using DiskApp.Infrastructure.FileSystem;

namespace DiskApp.Infrastructure.Tests.FileSystem;

/// <summary>
/// Запуск сканера на реальной файловой системе со сбором всех пакетов в список.
/// </summary>
internal static class ScanTestHelpers
{
    public static EntryFactory Entries { get; } = new(PathNormalizer.Current, FileKindMap.Default);

    public static Root RootFor(TemporaryDirectory folder, long id = 1) => new() { Id = id, Path = folder.FullPath, Label = "test" };

    public static async Task<(ScanSummary Summary, List<FileEntry> Entries, List<EntryBatch> Batches)> ScanAsync(
        ScanRequest request,
        PauseToken pause = default,
        CancellationToken cancellationToken = default)
    {
        var scanner = new DirectoryScanner(FileSystemLister.Instance, Entries, ThreadBackgroundPriority.Instance);
        var channel = Channel.CreateBounded<EntryBatch>(new BoundedChannelOptions(2) { SingleReader = true });
        var batches = new List<EntryBatch>();
        var consumer = Task.Run(async () =>
        {
            await foreach (var batch in channel.Reader.ReadAllAsync(cancellationToken))
            {
                batches.Add(batch);
            }
        }, cancellationToken);

        ScanSummary summary;
        try
        {
            summary = await scanner.ScanAsync(request, channel.Writer, new ScanCounters(), pause, cancellationToken);
        }
        finally
        {
            channel.Writer.TryComplete();
        }

        await consumer;
        return (summary, batches.SelectMany(static batch => batch.Entries).ToList(), batches);
    }
}

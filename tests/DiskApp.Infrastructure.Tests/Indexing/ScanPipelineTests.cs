using DiskApp.Core.Domain;
using DiskApp.Core.Events;
using DiskApp.Core.Indexing;

namespace DiskApp.Infrastructure.Tests.Indexing;

/// <summary>
/// Полное сканирование в индекс: первая индексация, повтор без изменений, mark-and-sweep удалённых, сохранение
/// недоступной папки, недоступный корень, поддерево, события прогресса и изменений, журнал.
/// </summary>
public sealed class ScanPipelineTests
{
    [Fact]
    public async Task RunAsync_FirstScan_IndexMatchesDisk()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        var created = TestTree.Generate(context.Files, files: 400, folders: 5, depth: 3);

        // Act
        var outcome = await context.FullScanAsync();

        // Assert
        outcome.Status.ShouldBe(ScanOutcomeStatus.Completed);
        outcome.Kind.ShouldBe(IndexTaskKind.FullScan);
        outcome.Written.Inserted.ShouldBe(created.Count);
        outcome.Found.ShouldBe(created.Count);
        outcome.Removed.ShouldBe(0);
        context.AssertIndexMatchesDisk();
    }

    [Fact]
    public async Task RunAsync_SecondScanWithoutChanges_OnlyMarksRows()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        var created = TestTree.Generate(context.Files, files: 120, folders: 3, depth: 2);
        var first = await context.FullScanAsync();

        // Act
        var second = await context.FullScanAsync();

        // Assert
        second.ScanId.ShouldBe(first.ScanId + 1);
        second.Written.ShouldBe(new Core.Repositories.UpsertResult(0, 0, created.Count));
        second.Removed.ShouldBe(0);
    }

    [Fact]
    public async Task RunAsync_FilesDeletedAndChangedWhileOffline_SweepsAndUpdates()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        var created = TestTree.Generate(context.Files, files: 60, folders: 3, depth: 2);
        await context.FullScanAsync();
        Directory.Delete(context.Files.Resolve("folder001"), recursive: true);
        File.Delete(context.Files.Resolve(FirstFileUnder(created, "folder000")));
        File.AppendAllText(context.Files.Resolve(FirstFileUnder(created, "folder002")), "more");
        context.Files.CreateFile(Path.Combine("folder000", "new.md"), "new");

        // Act
        var outcome = await context.FullScanAsync();

        // Assert
        outcome.Written.Inserted.ShouldBe(1);
        outcome.Written.Updated.ShouldBeGreaterThanOrEqualTo(1);
        outcome.Removed.ShouldBeGreaterThan(20);
        context.AssertIndexMatchesDisk();
    }

    [Fact]
    public async Task RunAsync_InaccessibleFolder_KeepsItsEntries()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        context.Files.CreateFile(Path.Combine("locked", "secret.txt"), "s");
        context.Files.CreateFile(Path.Combine("locked", "inner", "deep.txt"), "d");
        context.Files.CreateFile(Path.Combine("open", "visible.txt"), "v");
        await context.FullScanAsync();
        using var denied = TestTree.DenyAccess(context.Files, "locked");
        if (denied is null)
        {
            Assert.Skip("Запрет доступа к папке недоступен на этой ОС.");
        }

        // Act
        var outcome = await context.FullScanAsync();

        // Assert
        outcome.ErrorCount.ShouldBe(1);
        outcome.Removed.ShouldBe(0);
        var index = context.IndexSnapshot();
        index.Keys.ShouldContain(Path.Combine("locked", "secret.txt"));
        index.Keys.ShouldContain(Path.Combine("locked", "inner", "deep.txt"));
        var log = await context.ScanLog.GetRecentAsync(context.Root.Id, 10, TestContext.Current.CancellationToken);
        log.ShouldHaveSingleItem().Path.ShouldBe("locked");
    }

    [Fact]
    public async Task RunAsync_RootMissing_LeavesIndexUntouched()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        TestTree.Generate(context.Files, files: 10, folders: 1, depth: 1);
        await context.FullScanAsync();
        var before = context.IndexSnapshot();
        var offlineRequest = context.Request() with { Root = context.Root with { Path = context.Files.Resolve("unplugged-drive") } };

        // Act
        var outcome = await context.Pipeline.RunAsync(offlineRequest, PauseToken.None, null, TestContext.Current.CancellationToken);

        // Assert
        outcome.Status.ShouldBe(ScanOutcomeStatus.RootMissing);
        outcome.Removed.ShouldBe(0);
        context.IndexSnapshot().Keys.ShouldBe(before.Keys, ignoreOrder: true);
        (await context.ScanLog.GetRecentAsync(context.Root.Id, 5, TestContext.Current.CancellationToken))
            .ShouldContain(static entry => entry.Level == Core.Repositories.ScanLogLevel.Error);
    }

    [Fact]
    public async Task RunAsync_Subtree_SweepsOnlyInsideIt()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        context.Files.CreateFile(Path.Combine("docs", "a.txt"), "a");
        context.Files.CreateFile(Path.Combine("docs", "b.txt"), "b");
        context.Files.CreateFile(Path.Combine("other", "c.txt"), "c");
        await context.FullScanAsync();
        File.Delete(context.Files.Resolve(Path.Combine("docs", "b.txt")));
        File.Delete(context.Files.Resolve(Path.Combine("other", "c.txt")));
        context.Files.CreateFile(Path.Combine("docs", "new.txt"), "n");

        // Act
        var outcome = await context.FullScanAsync("docs");

        // Assert
        outcome.RelPath.ShouldBe("docs");
        outcome.Removed.ShouldBe(1);
        outcome.Written.Inserted.ShouldBe(1);
        var index = context.IndexSnapshot();
        index.Keys.ShouldContain(Path.Combine("other", "c.txt"), "вне поддерева индекс не трогается");
        index.Keys.ShouldNotContain(Path.Combine("docs", "b.txt"));
        index.Keys.ShouldContain(Path.Combine("docs", "new.txt"));
    }

    [Fact]
    public async Task RunAsync_SubtreeMissingOrExcluded_RemovesItFromIndex()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        context.Files.CreateFile(Path.Combine("gone", "a.txt"), "a");
        context.Files.CreateFile(Path.Combine("cache", "b.txt"), "b");
        await context.FullScanAsync();
        Directory.Delete(context.Files.Resolve("gone"), recursive: true);

        // Act
        var missing = await context.FullScanAsync("gone");
        var excluded = await context.FullScanAsync("cache", [ExclusionRule.Glob("cache")]);

        // Assert
        missing.Status.ShouldBe(ScanOutcomeStatus.SubtreeMissing);
        missing.Removed.ShouldBe(2);
        excluded.Status.ShouldBe(ScanOutcomeStatus.Excluded);
        excluded.Removed.ShouldBe(2);
        context.IndexSnapshot().ShouldBeEmpty();
    }

    [Fact]
    public async Task RunAsync_Events_ProgressPhasesAndFolderChanges()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        context.Files.CreateFile(Path.Combine("docs", "a.txt"), "a");
        context.Files.CreateFile(Path.Combine("pics", "b.jpg"), "b");
        var started = new List<long>();

        // Act
        var outcome = await context.Pipeline.RunAsync(context.Request(), PauseToken.None, started.Add, TestContext.Current.CancellationToken);

        // Assert
        started.ShouldBe([outcome.ScanId]);
        var phases = context.Progress.Select(static progress => progress.Phase).ToList();
        phases[0].ShouldBe(IndexPhase.Enumerating);
        phases.ShouldContain(IndexPhase.Sweeping);
        phases[^1].ShouldBe(IndexPhase.Done);
        context.Progress[^1].Written.ShouldBe(4);
        context.Progress.ShouldAllBe(progress => progress.RootId == context.Root.Id);
        var folders = context.Changes.SelectMany(static change => change.Changes).Select(static change => change.RelPath).ToList();
        folders.ShouldContain(string.Empty);
        folders.ShouldContain("docs");
        folders.ShouldContain("pics");
        context.Changes.SelectMany(static change => change.Changes).ShouldAllBe(static change => change.IsDirectory && change.Kind == EntryChangeKind.Modified);
    }

    [Fact]
    public async Task RunAsync_Cancelled_DoesNotSweep()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        TestTree.Generate(context.Files, files: 30, folders: 2, depth: 1);
        await context.FullScanAsync();
        var before = context.IndexSnapshot().Count;
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act
        var task = context.Pipeline.RunAsync(context.Request(), PauseToken.None, null, cancellation.Token);

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(task);
        context.IndexSnapshot().Count.ShouldBe(before);
    }

    private static string FirstFileUnder(IReadOnlyList<string> created, string folder)
        => created.First(path => path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal) && Path.HasExtension(path));
}

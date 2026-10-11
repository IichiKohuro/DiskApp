using DiskApp.Core.Abstractions;
using DiskApp.Core.Domain;
using DiskApp.Core.Events;
using DiskApp.Core.Indexing;
using DiskApp.Core.Paths;
using DiskApp.Infrastructure.FileSystem;

namespace DiskApp.Infrastructure.Tests.Indexing;

/// <summary>
/// Оркестратор на реальной файловой системе: первое сканирование при старте, сверка при повторном старте,
/// изменения через наблюдателя, недоступный корень и его возвращение, пропажа корня во время работы, пауза,
/// пересканирование и пересборка, подключение новых корней.
/// </summary>
public sealed class IndexerServiceTests
{
    private static readonly IndexerOptions FastOptions = new()
    {
        Exclusions = [],
        WatcherQuiet = TimeSpan.FromMilliseconds(100),
        WatcherMaxDelay = TimeSpan.FromMilliseconds(300),
        SchedulerTick = TimeSpan.FromMilliseconds(100),
        OfflinePollInterval = TimeSpan.FromMilliseconds(200),
        ReconcileInterval = TimeSpan.Zero,
        BatchSize = 100,
    };

    [Fact]
    public async Task StartAsync_NewRoot_FullScanIndexesEverything()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        TestTree.Generate(context.Files, files: 200, folders: 4, depth: 2);
        var statuses = new List<RootStatus>();
        context.Events.Subscribe<RootStatusChangedEvent>(e => { lock (statuses) { statuses.Add(e.Status); } });
        await using var indexer = CreateIndexer(context);

        // Act
        await indexer.StartAsync(TestContext.Current.CancellationToken);
        await indexer.WhenIdleAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Assert
        context.AssertIndexMatchesDisk();
        var status = indexer.GetStatuses().ShouldHaveSingleItem();
        status.Status.ShouldBe(RootStatus.Online);
        status.LastScanUtc.ShouldNotBeNull();
        status.RunningTask.ShouldBeNull();
        status.Progress!.Phase.ShouldBe(IndexPhase.Done);
        indexer.GetAggregateState().ShouldBe(IndexerState.Idle);
        lock (statuses)
        {
            statuses.ShouldContain(RootStatus.Scanning);
            statuses[^1].ShouldBe(RootStatus.Online);
        }

        (await context.Roots.GetByIdAsync(context.Root.Id, TestContext.Current.CancellationToken))!.LastScanUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task StartAsync_PreviouslyScannedRoot_ReconcilesOfflineChanges()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        TestTree.Generate(context.Files, files: 50, folders: 2, depth: 2);
        await using (var first = CreateIndexer(context))
        {
            await first.StartAsync(TestContext.Current.CancellationToken);
            await first.WhenIdleAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        Directory.Delete(context.Files.Resolve("folder001"), recursive: true);
        context.Files.CreateFile(Path.Combine("folder000", "while-offline.txt"), "o");
        context.Progress.Clear();

        // Act
        await using var second = CreateIndexer(context);
        await second.StartAsync(TestContext.Current.CancellationToken);
        await second.WhenIdleAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Assert
        context.AssertIndexMatchesDisk();
    }

    [Fact]
    public async Task Running_FileChanges_ReflectedThroughWatcher()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        context.Files.CreateFile(Path.Combine("docs", "a.txt"), "a");
        await using var indexer = CreateIndexer(context);
        await indexer.StartAsync(TestContext.Current.CancellationToken);
        await indexer.WhenIdleAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Act
        context.Files.CreateFile(Path.Combine("docs", "b.txt"), "b");
        context.Files.CreateFile(Path.Combine("moved", "deep", "c.txt"), "c");
        File.Delete(context.Files.Resolve(Path.Combine("docs", "a.txt")));

        // Assert
        await WaitUntilAsync(() => IndexMatchesDisk(context));
        await indexer.WhenIdleAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        context.AssertIndexMatchesDisk();
    }

    [Fact]
    public async Task StartAsync_OfflineRoot_ComesOnlineWhenFolderAppears()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        var drive = context.Files.Resolve("usb-drive");
        var root = await context.Roots.AddAsync(new Root { Path = drive, Label = "USB" }, TestContext.Current.CancellationToken);
        await using var indexer = CreateIndexer(context);

        // Act
        await indexer.StartAsync(TestContext.Current.CancellationToken);
        await indexer.WhenIdleAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var whileMissing = indexer.GetStatuses().Single(status => status.RootId == root.Id);
        var aggregateWhileMissing = indexer.GetAggregateState();
        context.Files.CreateFile(Path.Combine("usb-drive", "photo.jpg"), "p");

        // Assert
        whileMissing.Status.ShouldBe(RootStatus.Offline);
        aggregateWhileMissing.ShouldBe(IndexerState.PartiallyOffline);
        await WaitUntilAsync(async () => await context.Entries.GetByPathAsync(root.Id, "photo.jpg", TestContext.Current.CancellationToken) is not null);
        await WaitUntilAsync(() => indexer.GetStatuses().Single(status => status.RootId == root.Id).Status == RootStatus.Online);
    }

    [Fact]
    public async Task Running_RootDeleted_GoesOfflineAndKeepsIndex()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        context.Files.CreateFile(Path.Combine("network-share", "doc.txt"), "d");
        var share = context.Files.Resolve("network-share");
        var root = await context.Roots.AddAsync(new Root { Path = share, Label = "share" }, TestContext.Current.CancellationToken);
        await using var indexer = CreateIndexer(context);
        await indexer.StartAsync(TestContext.Current.CancellationToken);
        await indexer.WhenIdleAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        (await context.Entries.GetByPathAsync(root.Id, "doc.txt", TestContext.Current.CancellationToken)).ShouldNotBeNull();

        // Act: корень исчезает, как отключённый диск; наблюдатель либо падает, либо видит удаление — в обоих случаях сверка увидит пропажу
        Directory.Delete(share, recursive: true);
        indexer.ReconcileRoot(root.Id);

        // Assert
        await WaitUntilAsync(() => indexer.GetStatuses().Single(status => status.RootId == root.Id).Status == RootStatus.Offline);
        (await context.Entries.GetStatsAsync(root.Id, TestContext.Current.CancellationToken)).EntryCount.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task PauseAll_BeforeStart_HoldsTasksUntilResume()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        TestTree.Generate(context.Files, files: 30, folders: 2, depth: 1);
        await using var indexer = CreateIndexer(context);
        indexer.PauseAll();

        // Act
        await indexer.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        var indexedWhilePaused = context.IndexSnapshot().Count;
        var stateWhilePaused = indexer.GetAggregateState();
        indexer.ResumeAll();
        await indexer.WhenIdleAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Assert
        indexer.IsPaused.ShouldBeFalse();
        indexedWhilePaused.ShouldBe(0);
        stateWhilePaused.ShouldBe(IndexerState.Paused);
        context.AssertIndexMatchesDisk();
    }

    [Fact]
    public async Task PauseRoot_AfterInitialScan_HoldsRootTasksUntilResume()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        TestTree.Generate(context.Files, files: 20, folders: 2, depth: 1);
        await using var indexer = CreateIndexer(context);
        await indexer.StartAsync(TestContext.Current.CancellationToken);
        await indexer.WhenIdleAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Act
        indexer.PauseRoot(context.Root.Id).ShouldBeTrue();
        context.Files.CreateFile("added-while-paused.txt", "p");
        indexer.RescanRoot(context.Root.Id).ShouldBeTrue();
        await Task.Delay(600, TestContext.Current.CancellationToken);
        var whilePaused = indexer.GetStatuses().ShouldHaveSingleItem();
        var indexedWhilePaused = await context.Entries.GetByPathAsync(context.Root.Id, "added-while-paused.txt", TestContext.Current.CancellationToken);
        indexer.PauseAll();
        indexer.ResumeAll();
        var afterResumeAll = indexer.GetStatuses().ShouldHaveSingleItem();
        indexer.ResumeRoot(context.Root.Id).ShouldBeTrue();
        await indexer.WhenIdleAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Assert
        whilePaused.Status.ShouldBe(RootStatus.Paused);
        whilePaused.IsQueued.ShouldBeTrue();
        indexedWhilePaused.ShouldBeNull();
        afterResumeAll.Status.ShouldBe(RootStatus.Paused, "общее возобновление не снимает паузу корня");
        context.AssertIndexMatchesDisk();
        await WaitUntilAsync(() => indexer.GetStatuses()[0].Status == RootStatus.Online);
        indexer.PauseRoot(999).ShouldBeFalse("неизвестный корень");
    }

    [Fact]
    public async Task RescanAndRebuild_AfterInitialScan_IndexStaysComplete()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        TestTree.Generate(context.Files, files: 40, folders: 2, depth: 2);
        await using var indexer = CreateIndexer(context);
        await indexer.StartAsync(TestContext.Current.CancellationToken);
        await indexer.WhenIdleAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Act
        indexer.RescanRoot(context.Root.Id).ShouldBeTrue();
        await indexer.WhenIdleAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await indexer.RebuildAllAsync(TestContext.Current.CancellationToken);
        await indexer.WhenIdleAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Assert
        context.AssertIndexMatchesDisk();
        indexer.RescanRoot(999).ShouldBeFalse("неизвестный корень");
    }

    [Fact]
    public async Task RefreshRootsAsync_AddedAndRemovedRoots_AttachAndDetach()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        await using var indexer = CreateIndexer(context);
        await indexer.StartAsync(TestContext.Current.CancellationToken);
        await indexer.WhenIdleAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        context.Files.CreateFile(Path.Combine("second", "x.txt"), "x");
        var second = await context.Roots.AddAsync(new Root { Path = context.Files.Resolve("second"), Label = "second" }, TestContext.Current.CancellationToken);

        // Act
        await indexer.RefreshRootsAsync(TestContext.Current.CancellationToken);
        await indexer.WhenIdleAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var afterAdd = indexer.GetStatuses().Select(static status => status.RootId).ToArray();
        await context.Roots.UpdateAsync(second with { IsEnabled = false }, TestContext.Current.CancellationToken);
        await indexer.RefreshRootsAsync(TestContext.Current.CancellationToken);

        // Assert
        afterAdd.ShouldBe([context.Root.Id, second.Id]);
        (await context.Entries.GetByPathAsync(second.Id, "x.txt", TestContext.Current.CancellationToken)).ShouldNotBeNull();
        indexer.GetStatuses().Select(static status => status.RootId).ShouldBe([context.Root.Id]);
    }

    private static IndexerService CreateIndexer(IndexingTestContext context, IndexerOptions? options = null)
        => new(
            context.Roots,
            context.Entries,
            context.ScanLog,
            FileSystemLister.Instance,
            new FileSystemRootWatcherFactory(PathNormalizer.Current),
            new DriveInfoProvider(),
            context.Events,
            SystemClock.Instance,
            context.Factory,
            options ?? FastOptions,
            ThreadBackgroundPriority.Instance);

    private static bool IndexMatchesDisk(IndexingTestContext context)
    {
        var index = context.IndexSnapshot();
        var disk = context.DiskSnapshot();
        return index.Count == disk.Count && disk.All(pair => index.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }

    private static Task WaitUntilAsync(Func<bool> condition) => WaitUntilAsync(() => Task.FromResult(condition()));

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        (await condition()).ShouldBeTrue("условие не выполнилось за отведённое время");
    }
}

using System.Collections.Concurrent;

using DiskApp.Core.Abstractions;
using DiskApp.Core.Indexing;
using DiskApp.Core.Indexing.Watching;
using DiskApp.Core.Paths;
using DiskApp.Infrastructure.FileSystem;

namespace DiskApp.Infrastructure.Tests.Indexing;

/// <summary>
/// Наблюдатель на реальной файловой системе: создание, изменение, удаление и переименование файла и папки отражаются
/// в индексе меньше чем за секунду-две; тысяча быстрых изменений оставляет индекс согласованным; переполнение
/// и сбой превращаются в запросы оркестратору; пауза копит изменения.
/// </summary>
public sealed class RootChangeProcessorTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task Watcher_FileAndFolderOperations_ReflectedInIndex()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        context.Files.CreateFile(Path.Combine("docs", "keep.txt"), "k");
        context.Files.CreateFile(Path.Combine("docs", "gone.txt"), "g");
        context.Files.CreateFile(Path.Combine("old", "inner.txt"), "i");
        await context.FullScanAsync();
        await using var processor = StartProcessor(context, out _);

        // Act
        context.Files.CreateFile(Path.Combine("docs", "new.txt"), "n");
        File.AppendAllText(context.Files.Resolve(Path.Combine("docs", "keep.txt")), "changed");
        File.Delete(context.Files.Resolve(Path.Combine("docs", "gone.txt")));
        Directory.Move(context.Files.Resolve("old"), context.Files.Resolve("renamed"));
        context.Files.CreateFile(Path.Combine("fresh", "file.pdf"), "f");

        // Assert
        await WaitUntilAsync(() => IndexMatchesDisk(context));
        context.AssertIndexMatchesDisk();
    }

    [Fact]
    public async Task Watcher_SingleChange_ReflectedWithinBudget()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        context.Files.CreateDirectory("docs");
        await context.FullScanAsync();
        await using var processor = StartProcessor(context, out _);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Act
        context.Files.CreateFile(Path.Combine("docs", "quick.txt"), "q");
        await WaitUntilAsync(async () => await context.Entries.GetByPathAsync(context.Root.Id, Path.Combine("docs", "quick.txt"), TestContext.Current.CancellationToken) is not null);

        // Assert: бюджет PLAN.md ≤ 1 с при паузе склейки 400 мс; в тесте пауза 150 мс
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Watcher_ThousandRapidChanges_IndexConsistent()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        context.Files.CreateDirectory("burst");
        await context.FullScanAsync();
        await using var processor = StartProcessor(context, out var requests);

        // Act: создаём, переписываем, переименовываем и удаляем вперемешку
        for (var i = 0; i < 400; i++)
        {
            context.Files.CreateFile(Path.Combine("burst", $"f{i:000}.txt"), "x");
        }

        for (var i = 0; i < 400; i += 2)
        {
            File.AppendAllText(context.Files.Resolve(Path.Combine("burst", $"f{i:000}.txt")), "y");
        }

        for (var i = 1; i < 400; i += 4)
        {
            File.Move(context.Files.Resolve(Path.Combine("burst", $"f{i:000}.txt")), context.Files.Resolve(Path.Combine("burst", $"r{i:000}.txt")));
        }

        for (var i = 3; i < 400; i += 4)
        {
            File.Delete(context.Files.Resolve(Path.Combine("burst", $"f{i:000}.txt")));
        }

        // Assert: либо события применены, либо при переполнении запрошена сверка — тогда выполняем её как оркестратор
        await WaitUntilAsync(async () =>
        {
            if (requests.TryDequeue(out var request) && request.Kind == RootRequestKind.Reconcile)
            {
                await context.ReconcileAsync(request.RelPath);
            }

            return IndexMatchesDisk(context);
        }, TimeSpan.FromSeconds(20));
        context.AssertIndexMatchesDisk();
    }

    [Fact]
    public async Task Processor_OverflowAndFailure_BecomeRequests()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        var watcher = new FakeWatcher();
        var requests = new ConcurrentQueue<RootRequest>();
        await using var processor = CreateProcessor(context, watcher, requests, PauseToken.None);
        processor.Start();

        // Act
        watcher.RaiseFailure(new WatcherFailure(true, "overflow"));
        watcher.RaiseFailure(new WatcherFailure(false, "network gone"));

        // Assert
        await WaitUntilAsync(() => requests.Count >= 2);
        requests.ShouldContain(static request => request.Kind == RootRequestKind.Reconcile && request.RelPath.Length == 0);
        requests.ShouldContain(static request => request.Kind == RootRequestKind.CheckAvailability);
    }

    [Fact]
    public async Task Processor_Paused_AppliesAfterResume()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        await context.FullScanAsync();
        var watcher = new FakeWatcher();
        var pause = new PauseTokenSource();
        pause.Pause();
        await using var processor = CreateProcessor(context, watcher, new ConcurrentQueue<RootRequest>(), pause.Token);
        processor.Start();
        context.Files.CreateFile("while-paused.txt", "p");

        // Act
        watcher.Raise(new RawChange(RawChangeKind.Created, "while-paused.txt"));
        await Task.Delay(Quiet + MaxDelay + TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        var appliedWhilePaused = processor.AppliedBatches;
        pause.Resume();

        // Assert
        appliedWhilePaused.ShouldBe(0);
        await WaitUntilAsync(async () => await context.Entries.GetByPathAsync(context.Root.Id, "while-paused.txt", TestContext.Current.CancellationToken) is not null);
    }

    [Fact]
    public async Task Processor_NewFolder_RequestsSubtreeReconcile()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        await context.FullScanAsync();
        var watcher = new FakeWatcher();
        var requests = new ConcurrentQueue<RootRequest>();
        await using var processor = CreateProcessor(context, watcher, requests, PauseToken.None);
        processor.Start();
        context.Files.CreateFile(Path.Combine("moved-in", "content.txt"), "c");

        // Act: наблюдатель сообщает только о папке, как при переносе в корень извне
        watcher.Raise(new RawChange(RawChangeKind.Created, "moved-in"));

        // Assert
        await WaitUntilAsync(() => requests.Any(static request => request.RelPath == "moved-in"));
        var request = requests.Single(static request => request.RelPath == "moved-in");
        request.Kind.ShouldBe(RootRequestKind.Reconcile);
        await context.ReconcileAsync(request.RelPath);
        context.AssertIndexMatchesDisk();
    }

    private static RootChangeProcessor StartProcessor(IndexingTestContext context, out ConcurrentQueue<RootRequest> requests)
    {
        requests = new ConcurrentQueue<RootRequest>();
        var processor = CreateProcessor(context, new FileSystemRootWatcher(context.Root, PathNormalizer.Current), requests, PauseToken.None);
        processor.Start();
        return processor;
    }

    private static RootChangeProcessor CreateProcessor(IndexingTestContext context, IRootWatcher watcher, ConcurrentQueue<RootRequest> requests, PauseToken pause)
    {
        var applier = new ChangeApplier(FileSystemLister.Instance, context.Entries, context.Events, context.Factory);
        var matcher = ExclusionMatcher.Create([], context.Root, PathNormalizer.Current);
        return new RootChangeProcessor(
            context.Root,
            watcher,
            applier,
            () => matcher,
            new RootIndexState(PathNormalizer.Current),
            new ChangeCoalescer(PathNormalizer.Current, Quiet, MaxDelay),
            SystemClock.Instance,
            pause,
            requests.Enqueue);
    }

    private static bool IndexMatchesDisk(IndexingTestContext context)
    {
        var index = context.IndexSnapshot();
        var disk = context.DiskSnapshot();
        return index.Count == disk.Count && disk.All(pair => index.TryGetValue(pair.Key, out var value) && value == pair.Value);
    }

    private static Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
        => WaitUntilAsync(() => Task.FromResult(condition()), timeout);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
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

    private sealed class FakeWatcher : IRootWatcher
    {
        public event EventHandler<RawChange>? Changed;

        public event EventHandler<WatcherFailure>? Failed;

        public void Start()
        {
        }

        public void Raise(RawChange change) => Changed?.Invoke(this, change);

        public void RaiseFailure(WatcherFailure failure) => Failed?.Invoke(this, failure);

        public void Dispose()
        {
        }
    }
}

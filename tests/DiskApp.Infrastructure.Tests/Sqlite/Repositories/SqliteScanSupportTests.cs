using DiskApp.Core.Repositories;

using static DiskApp.Infrastructure.Tests.Sqlite.Repositories.IndexTestContext;

namespace DiskApp.Infrastructure.Tests.Sqlite.Repositories;

/// <summary>
/// Методы репозиториев для сканера и сверки: номер сканирования, пометка и sweep поддерева, детальный upsert,
/// дети по пути, журнал сканирования.
/// </summary>
public sealed class SqliteScanSupportTests
{
    [Fact]
    public async Task NextScanIdAsync_EmptyAndAfterScan_IncrementsFromMax()
    {
        // Arrange
        await using var context = await CreateAsync();

        // Act
        var first = await context.Entries.NextScanIdAsync(context.Root.Id, TestContext.Current.CancellationToken);
        await context.UpsertAsync(7, context.Entry("a.txt"));
        var next = await context.Entries.NextScanIdAsync(context.Root.Id, TestContext.Current.CancellationToken);

        // Assert
        first.ShouldBe(1);
        next.ShouldBe(8);
    }

    [Fact]
    public async Task MarkSubtreeAndSweepSubtree_FailedFolderIsKeptOthersSwept()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.UpsertAsync(1,
            context.Entry("locked", isDirectory: true),
            context.Entry("locked/secret.txt"),
            context.Entry("docs", isDirectory: true),
            context.Entry("docs/old.txt"),
            context.Entry("docs/keep.txt"));

        // Act: второе сканирование увидело docs/keep.txt, а папку locked прочитать не смогло
        await context.UpsertAsync(2, context.Entry("docs", isDirectory: true), context.Entry("docs/keep.txt"));
        var marked = await context.Entries.MarkSubtreeAsync(context.Root.Id, "locked", 2, TestContext.Current.CancellationToken);
        var swept = await context.Entries.SweepAsync(context.Root.Id, 2, TestContext.Current.CancellationToken);

        // Assert
        marked.ShouldBe(2);
        swept.ShouldBe(1);
        (await context.GetAsync("locked/secret.txt")).Name.ShouldBe("secret.txt");
        (await context.Entries.GetByPathAsync(context.Root.Id, P("docs/old.txt"), TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task SweepSubtreeAsync_RemovesOnlyUnmarkedDescendants()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.UpsertAsync(1,
            context.Entry("docs", isDirectory: true),
            context.Entry("docs/a.txt"),
            context.Entry("docs/b.txt"),
            context.Entry("other.txt"));
        await context.UpsertAsync(5, context.Entry("docs/a.txt"));

        // Act
        var swept = await context.Entries.SweepSubtreeAsync(context.Root.Id, "docs", 5, TestContext.Current.CancellationToken);

        // Assert
        swept.ShouldBe(1, "удалена только docs/b.txt; сама папка и записи вне неё не трогаются");
        (await context.GetAsync("docs")).IsDirectory.ShouldBeTrue();
        (await context.GetAsync("other.txt")).Name.ShouldBe("other.txt");
        (await context.Entries.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(3);
    }

    [Fact]
    public async Task MarkSubtreeAsync_EmptyPath_MarksWholeRoot()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.UpsertAsync(1, context.Entry("a.txt"), context.Entry("b.txt"));

        // Act
        var marked = await context.Entries.MarkSubtreeAsync(context.Root.Id, string.Empty, 3, TestContext.Current.CancellationToken);
        var swept = await context.Entries.SweepSubtreeAsync(context.Root.Id, string.Empty, 3, TestContext.Current.CancellationToken);

        // Assert
        marked.ShouldBe(2);
        swept.ShouldBe(0);
    }

    [Fact]
    public async Task UpsertDetailedAsync_MixedBatch_ReportsIndicesOfInsertedAndUpdated()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.UpsertAsync(1, context.Entry("same.txt"), context.Entry("changed.txt", size: 1));

        // Act
        var details = await context.Entries.UpsertDetailedAsync(
            [context.Entry("new.txt"), context.Entry("same.txt"), context.Entry("changed.txt", size: 2)],
            2,
            TestContext.Current.CancellationToken);
        var empty = await context.Entries.UpsertDetailedAsync([], 2, TestContext.Current.CancellationToken);

        // Assert
        details.Totals.ShouldBe(new UpsertResult(1, 1, 1));
        details.InsertedIndices.ShouldBe([0]);
        details.UpdatedIndices.ShouldBe([2]);
        empty.ShouldBeSameAs(UpsertDetails.Empty);
    }

    [Fact]
    public async Task GetChildrenOfPathAsync_RootAndFolder_ReturnDirectChildrenOnly()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.UpsertAsync(1,
            context.Entry("docs", isDirectory: true),
            context.Entry("docs/a.txt"),
            context.Entry("docs/sub", isDirectory: true),
            context.Entry("docs/sub/deep.txt"),
            context.Entry("top.txt"));

        // Act
        var top = await context.Entries.GetChildrenOfPathAsync(context.Root.Id, string.Empty, TestContext.Current.CancellationToken);
        var docs = await context.Entries.GetChildrenOfPathAsync(context.Root.Id, "docs", TestContext.Current.CancellationToken);
        var missing = await context.Entries.GetChildrenOfPathAsync(context.Root.Id, "nope", TestContext.Current.CancellationToken);

        // Assert
        top.Select(static entry => entry.Name).ShouldBe(["docs", "top.txt"], ignoreOrder: true);
        docs.Select(static entry => entry.Name).ShouldBe(["a.txt", "sub"], ignoreOrder: true);
        missing.ShouldBeEmpty();
    }

    [Fact]
    public async Task ScanLog_AddRecentPrune_KeepsNewestFirst()
    {
        // Arrange
        await using var context = await CreateAsync();
        var log = new Infrastructure.Sqlite.Repositories.SqliteScanLogRepository(context.Database);
        var entries = Enumerable.Range(1, 5)
            .Select(i => new ScanLogEntry(context.Root.Id, 1, Now.AddSeconds(i), ScanLogLevel.Warning, $"dir{i}", $"Нет доступа {i}"))
            .ToArray();

        // Act
        await log.AddAsync(entries, TestContext.Current.CancellationToken);
        await log.AddAsync([], TestContext.Current.CancellationToken);
        var recent = await log.GetRecentAsync(context.Root.Id, 3, TestContext.Current.CancellationToken);
        var pruned = await log.PruneAsync(context.Root.Id, 2, TestContext.Current.CancellationToken);
        var left = await log.GetRecentAsync(context.Root.Id, 10, TestContext.Current.CancellationToken);

        // Assert
        recent.Select(static entry => entry.Path).ShouldBe(["dir5", "dir4", "dir3"]);
        recent[0].Level.ShouldBe(ScanLogLevel.Warning);
        recent[0].OccurredUtc.ShouldBe(Now.AddSeconds(5));
        recent[0].Id.ShouldBeGreaterThan(0);
        pruned.ShouldBe(3);
        left.Select(static entry => entry.Message).ShouldBe(["Нет доступа 5", "Нет доступа 4"]);
    }
}

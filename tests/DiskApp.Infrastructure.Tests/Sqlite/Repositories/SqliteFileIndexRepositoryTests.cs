using Dapper;

using DiskApp.Core.Domain;
using DiskApp.Core.Search;

using static DiskApp.Infrastructure.Tests.Sqlite.Repositories.IndexTestContext;

namespace DiskApp.Infrastructure.Tests.Sqlite.Repositories;

/// <summary>
/// Записи индекса: пакетная запись с разбором родителей и учётом неизменённых, mark-and-sweep, удаление и переименование
/// поддеревьев диапазоном, дети с сортировкой и страницами, сводка, целостность после нагрузки.
/// </summary>
public sealed class SqliteFileIndexRepositoryTests
{
    [Fact]
    public async Task UpsertAsync_NewTree_InsertsAndResolvesParents()
    {
        // Arrange
        await using var context = await CreateAsync();

        // Act
        var result = await context.UpsertAsync(1,
            context.Entry("docs", isDirectory: true),
            context.Entry("docs/report.pdf"),
            context.Entry("docs/2026", isDirectory: true),
            context.Entry("docs/2026/plan.xlsx"),
            context.Entry("readme.md"));

        // Assert
        result.ShouldBe(new Core.Repositories.UpsertResult(5, 0, 0));
        var docs = await context.GetAsync("docs");
        var year = await context.GetAsync("docs/2026");
        var report = await context.GetAsync("docs/report.pdf");
        var plan = await context.GetAsync("docs/2026/plan.xlsx");
        var readme = await context.GetAsync("readme.md");
        docs.ParentId.ShouldBeNull();
        docs.Kind.ShouldBe(FileKind.Folder);
        report.ParentId.ShouldBe(docs.Id);
        report.Kind.ShouldBe(FileKind.Pdf);
        report.Extension.ShouldBe("pdf");
        plan.ParentId.ShouldBe(year.Id);
        plan.Depth.ShouldBe(3);
        readme.ParentId.ShouldBeNull();
        readme.ModifiedUtc.ShouldBe(Now.AddDays(-1));
        (await context.Entries.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(5);
    }

    [Fact]
    public async Task UpsertAsync_SameEntriesAgain_MarksUnchangedAndSweepKeepsThem()
    {
        // Arrange
        await using var context = await CreateAsync();
        var entries = new[] { context.Entry("a", isDirectory: true), context.Entry("a/b.txt"), context.Entry("c.txt") };
        await context.UpsertAsync(1, entries);

        // Act
        var second = await context.UpsertAsync(2, entries);
        var swept = await context.Entries.SweepAsync(context.Root.Id, 2, TestContext.Current.CancellationToken);

        // Assert
        second.ShouldBe(new Core.Repositories.UpsertResult(0, 0, 3));
        swept.ShouldBe(0);
        (await context.Entries.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(3);
    }

    [Fact]
    public async Task UpsertAsync_ChangedSize_UpdatesRowKeepingId()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.UpsertAsync(1, context.Entry("notes.txt", size: 10));
        var before = await context.GetAsync("notes.txt");

        // Act
        var result = await context.UpsertAsync(2, context.Entry("notes.txt", size: 20, modified: Now));

        // Assert
        result.ShouldBe(new Core.Repositories.UpsertResult(0, 1, 0));
        var after = await context.GetAsync("notes.txt");
        after.Id.ShouldBe(before.Id);
        after.Size.ShouldBe(20);
        after.ModifiedUtc.ShouldBe(Now);
    }

    [Fact]
    public async Task SweepAsync_EntriesWithoutCurrentScan_AreRemoved()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.UpsertAsync(1, context.Entry("keep.txt"), context.Entry("gone.txt"), context.Entry("old", isDirectory: true), context.Entry("old/x.txt"));
        await context.UpsertAsync(2, context.Entry("keep.txt"));

        // Act
        var swept = await context.Entries.SweepAsync(context.Root.Id, 2, TestContext.Current.CancellationToken);

        // Assert
        swept.ShouldBe(3);
        (await context.Entries.GetByPathAsync(context.Root.Id, "gone.txt", TestContext.Current.CancellationToken)).ShouldBeNull();
        (await context.Entries.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
    }

    [Fact]
    public async Task DeleteSubtreeAsync_Directory_RemovesItAndDescendantsOnly()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.UpsertAsync(1,
            context.Entry("docs", isDirectory: true),
            context.Entry("docs/a.txt"),
            context.Entry("docs/sub", isDirectory: true),
            context.Entry("docs/sub/b.txt"),
            context.Entry("docs2", isDirectory: true),
            context.Entry("docs2/c.txt"));

        // Act
        var deleted = await context.Entries.DeleteSubtreeAsync(context.Root.Id, "docs", TestContext.Current.CancellationToken);

        // Assert
        deleted.ShouldBe(4);
        (await context.Entries.GetByPathAsync(context.Root.Id, P("docs/sub/b.txt"), TestContext.Current.CancellationToken)).ShouldBeNull();
        (await context.GetAsync("docs2/c.txt")).Name.ShouldBe("c.txt");
        (await context.Entries.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(2);
    }

    [Fact]
    public async Task RenameSubtreeAsync_Directory_UpdatesPathsNameParentAndFts()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.UpsertAsync(1,
            context.Entry("docs", isDirectory: true),
            context.Entry("docs/sub", isDirectory: true),
            context.Entry("docs/sub/b.txt"),
            context.Entry("docs/sub/deep", isDirectory: true),
            context.Entry("docs/sub/deep/c.txt"),
            context.Entry("archive", isDirectory: true));
        var archive = await context.GetAsync("archive");

        // Act
        var affected = await context.Entries.RenameSubtreeAsync(context.Root.Id, P("docs/sub"), P("archive/moved"), TestContext.Current.CancellationToken);

        // Assert
        affected.ShouldBe(4);
        var moved = await context.GetAsync("archive/moved");
        moved.Name.ShouldBe("moved");
        moved.ParentId.ShouldBe(archive.Id);
        moved.IsDirectory.ShouldBeTrue();
        (await context.GetAsync("archive/moved/b.txt")).ParentId.ShouldBe(moved.Id);
        (await context.GetAsync("archive/moved/deep/c.txt")).Depth.ShouldBe(4);
        (await context.Entries.GetByPathAsync(context.Root.Id, P("docs/sub/b.txt"), TestContext.Current.CancellationToken)).ShouldBeNull();
        using var reader = context.Database.Connections.Open();
        reader.Query<string>("SELECT e.name FROM entries_fts JOIN entries AS e ON e.id = entries_fts.rowid WHERE entries_fts MATCH '\"moved\"'").ShouldBe(["moved"]);
        reader.Query<string>("SELECT e.name FROM entries_fts JOIN entries AS e ON e.id = entries_fts.rowid WHERE entries_fts MATCH '\"sub\"'").ShouldBeEmpty();
    }

    [Fact]
    public async Task RenameSubtreeAsync_File_UpdatesExtensionAndKind()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.UpsertAsync(1, context.Entry("data.csv"));

        // Act
        var affected = await context.Entries.RenameSubtreeAsync(context.Root.Id, "data.csv", "Data.json", TestContext.Current.CancellationToken);

        // Assert
        affected.ShouldBe(1);
        var renamed = await context.GetAsync("Data.json");
        renamed.Name.ShouldBe("Data.json");
        renamed.Extension.ShouldBe("json");
        renamed.Kind.ShouldBe(FileKind.Code);
        renamed.IsDirectory.ShouldBeFalse();
    }

    [Fact]
    public async Task RenameSubtreeAsync_MissingPath_ReturnsZero()
    {
        // Arrange
        await using var context = await CreateAsync();

        // Act
        var affected = await context.Entries.RenameSubtreeAsync(context.Root.Id, "nothing", "else", TestContext.Current.CancellationToken);

        // Assert
        affected.ShouldBe(0);
    }

    [Fact]
    public async Task GetChildrenAsync_FoldersFirstThenSortedAndPaged()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.UpsertAsync(1,
            context.Entry("b.txt", size: 5),
            context.Entry("a.txt", size: 10),
            context.Entry("z", isDirectory: true),
            context.Entry("m", isDirectory: true),
            context.Entry("m/inner.txt"));

        // Act
        var byName = await context.Entries.GetChildrenAsync(context.Root.Id, null, SearchSortField.Name, descending: false, 0, 10, TestContext.Current.CancellationToken);
        var bySizeDescending = await context.Entries.GetChildrenAsync(context.Root.Id, null, SearchSortField.Size, descending: true, 0, 10, TestContext.Current.CancellationToken);
        var page = await context.Entries.GetChildrenAsync(context.Root.Id, null, SearchSortField.Name, descending: false, 1, 2, TestContext.Current.CancellationToken);
        var folderM = await context.GetAsync("m");
        var inner = await context.Entries.GetChildrenAsync(context.Root.Id, folderM.Id, SearchSortField.Name, descending: false, 0, 10, TestContext.Current.CancellationToken);

        // Assert
        byName.Select(static entry => entry.Name).ShouldBe(["m", "z", "a.txt", "b.txt"]);
        bySizeDescending.Select(static entry => entry.Name).ShouldBe(["z", "m", "a.txt", "b.txt"]);
        page.Select(static entry => entry.Name).ShouldBe(["z", "a.txt"]);
        inner.Select(static entry => entry.Name).ShouldBe(["inner.txt"]);
    }

    [Fact]
    public async Task GetByIdAndGetMany_ExistingAndMissingIds_ReturnOnlyExisting()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.UpsertAsync(1, context.Entry("a.txt"), context.Entry("b.txt"));
        var a = await context.GetAsync("a.txt");
        var b = await context.GetAsync("b.txt");

        // Act
        var byId = await context.Entries.GetByIdAsync(a.Id, TestContext.Current.CancellationToken);
        var missing = await context.Entries.GetByIdAsync(999_999, TestContext.Current.CancellationToken);
        var many = await context.Entries.GetManyAsync([b.Id, 999_999, a.Id], TestContext.Current.CancellationToken);
        var none = await context.Entries.GetManyAsync([], TestContext.Current.CancellationToken);

        // Assert
        byId.ShouldBe(a);
        missing.ShouldBeNull();
        many.Select(static entry => entry.Name).ShouldBe(["a.txt", "b.txt"], ignoreOrder: true);
        none.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetStatsAsync_AfterUpsert_CountsFilesDirectoriesAndSize()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.UpsertAsync(1,
            context.Entry("d", isDirectory: true, size: 999),
            context.Entry("d/a.txt", size: 10),
            context.Entry("b.txt", size: 32));

        // Act
        var stats = await context.Entries.GetStatsAsync(context.Root.Id, TestContext.Current.CancellationToken);
        var emptyStats = await context.Entries.GetStatsAsync(context.Root.Id + 1, TestContext.Current.CancellationToken);

        // Assert
        stats.ShouldBe(new Core.Repositories.IndexStats(context.Root.Id, 2, 1, 42));
        stats.EntryCount.ShouldBe(3);
        emptyStats.EntryCount.ShouldBe(0);
    }

    [Fact]
    public async Task DeleteByRootAsync_RemovesAllEntriesOfRoot()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.UpsertAsync(1, context.Entry("a.txt"), context.Entry("b.txt"));

        // Act
        var deleted = await context.Entries.DeleteByRootAsync(context.Root.Id, TestContext.Current.CancellationToken);

        // Assert
        deleted.ShouldBe(2);
        (await context.Entries.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task UpsertAsync_CancelledToken_WritesNothing()
    {
        // Arrange
        await using var context = await CreateAsync();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act
        var task = context.Entries.UpsertAsync([context.Entry("a.txt")], 1, cancellation.Token);

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(task);
        (await context.Entries.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task UpsertAsync_TwentyThousandEntriesInBatches_PassesQuickCheckAndStaysReadable()
    {
        // Arrange
        await using var context = await CreateAsync();
        const int folders = 40;
        const int filesPerFolder = 499;
        var entries = new List<FileEntry>();
        for (var folder = 0; folder < folders; folder++)
        {
            entries.Add(context.Entry($"folder{folder:000}", isDirectory: true));
            for (var file = 0; file < filesPerFolder; file++)
            {
                entries.Add(context.Entry($"folder{folder:000}/file{file:000}.txt", size: file));
            }
        }

        // Act
        var total = default(Core.Repositories.UpsertResult);
        foreach (var batch in entries.Chunk(5_000))
        {
            total += await context.Entries.UpsertAsync(batch, 1, TestContext.Current.CancellationToken);
        }

        var readDuringIdle = await context.Entries.GetChildrenAsync(context.Root.Id, (await context.GetAsync("folder007")).Id, SearchSortField.Name, false, 0, 3, TestContext.Current.CancellationToken);

        // Assert
        total.Inserted.ShouldBe(folders * (filesPerFolder + 1));
        readDuringIdle.Select(static entry => entry.Name).ShouldBe(["file000.txt", "file001.txt", "file002.txt"]);
        (await context.Entries.GetStatsAsync(context.Root.Id, TestContext.Current.CancellationToken)).FileCount.ShouldBe(folders * filesPerFolder);
        context.Database.CheckIntegrity().ShouldBeTrue();
    }
}

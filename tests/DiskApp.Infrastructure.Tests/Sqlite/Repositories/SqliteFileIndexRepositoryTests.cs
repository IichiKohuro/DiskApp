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
    public async Task UpsertAsync_EntryWrittenBeforeItsParent_NextScanLinksParent()
    {
        // Arrange: наблюдатель записал файл, пока пересборка индекса ещё не дошла до его папки
        await using var context = await CreateAsync();
        await context.UpsertAsync(1, context.Entry("docs/report.pdf"));
        (await context.GetAsync("docs/report.pdf")).ParentId.ShouldBeNull();

        // Act
        var result = await context.UpsertAsync(2, context.Entry("docs", isDirectory: true), context.Entry("docs/report.pdf"));

        // Assert
        result.ShouldBe(new Core.Repositories.UpsertResult(1, 1, 0));
        var docs = await context.GetAsync("docs");
        (await context.GetAsync("docs/report.pdf")).ParentId.ShouldBe(docs.Id);
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
    public async Task MoveSubtreeAsync_FolderToNewRoot_KeepsIdsReparentsTopLevelAndReplacesStaleRows()
    {
        // Arrange: папка TCI2 внешнего корня становится отдельным корнем (ADR-0015); у нового корня уже есть устаревшая запись.
        await using var context = await CreateAsync();
        await context.UpsertAsync(1,
            context.Entry("10S-417", isDirectory: true),
            context.Entry("10S-417/TCI2", isDirectory: true),
            context.Entry("10S-417/TCI2/yml", isDirectory: true),
            context.Entry("10S-417/TCI2/yml/a.yml"),
            context.Entry("10S-417/TCI2/readme.md"),
            context.Entry("10S-417/other.txt"));
        var inner = await context.Roots.AddAsync(new Root { Path = Path.Combine(context.Root.Path, "10S-417", "TCI2"), Label = "TCI2" }, TestContext.Current.CancellationToken);
        await context.Entries.UpsertAsync([FileEntry.Create(inner.Id, null, "readme.md", false, 1, Now, Now, FileAttributes.Normal)], 1, TestContext.Current.CancellationToken);
        var readme = await context.GetAsync("10S-417/TCI2/readme.md");
        var yml = await context.GetAsync("10S-417/TCI2/yml/a.yml");
        await context.Usage.RecordOpenAsync(readme.Id, Now, TestContext.Current.CancellationToken);

        // Act
        var moved = await context.Entries.MoveSubtreeAsync(context.Root.Id, P("10S-417/TCI2"), inner.Id, string.Empty, TestContext.Current.CancellationToken);

        // Assert
        moved.ShouldBe(3, "перенесены потомки, сама папка TCI2 удалена");
        var movedReadme = (await context.Entries.GetByPathAsync(inner.Id, "readme.md", TestContext.Current.CancellationToken)).ShouldNotBeNull();
        movedReadme.Id.ShouldBe(readme.Id, "идентификатор сохраняется, устаревшая запись приёмника заменена");
        movedReadme.ParentId.ShouldBeNull("верхний уровень нового корня");
        var movedFolder = (await context.Entries.GetByPathAsync(inner.Id, "yml", TestContext.Current.CancellationToken)).ShouldNotBeNull();
        var movedYml = (await context.Entries.GetByPathAsync(inner.Id, P("yml/a.yml"), TestContext.Current.CancellationToken)).ShouldNotBeNull();
        movedYml.Id.ShouldBe(yml.Id);
        movedYml.ParentId.ShouldBe(movedFolder.Id);
        (await context.Usage.GetAsync(readme.Id, TestContext.Current.CancellationToken)).ShouldNotBeNull().OpenCount.ShouldBe(1);
        (await context.Entries.GetByPathAsync(context.Root.Id, P("10S-417/TCI2"), TestContext.Current.CancellationToken)).ShouldBeNull("папка-источник удалена");
        (await context.Entries.GetByPathAsync(context.Root.Id, P("10S-417/TCI2/yml/a.yml"), TestContext.Current.CancellationToken)).ShouldBeNull();
        (await context.GetAsync("10S-417/other.txt")).ShouldNotBeNull();
        (await context.Entries.GetStatsAsync(inner.Id, TestContext.Current.CancellationToken)).FileCount.ShouldBe(2);
        using var reader = context.Database.Connections.Open();
        reader.Query<long>("SELECT e.root_id FROM entries_fts JOIN entries AS e ON e.id = entries_fts.rowid WHERE entries_fts MATCH '\"readme\"'").ShouldBe([inner.Id]);
    }

    [Fact]
    public async Task MoveSubtreeAsync_WholeRootIntoFolder_CreatesPlaceholderFolderForTopLevel()
    {
        // Arrange: внутренний корень удаляют, его записи возвращаются внешнему под папкой 10S-417\TCI2, записи которой у внешнего нет.
        await using var context = await CreateAsync();
        await context.UpsertAsync(1, context.Entry("10S-417", isDirectory: true), context.Entry("10S-417/other.txt"));
        var order = await context.GetAsync("10S-417");
        var inner = await context.Roots.AddAsync(new Root { Path = Path.Combine(context.Root.Path, "10S-417", "TCI2"), Label = "TCI2" }, TestContext.Current.CancellationToken);
        await context.Entries.UpsertAsync(
            [
                FileEntry.Create(inner.Id, null, "yml", true, 0, Now, Now, FileAttributes.Directory),
                FileEntry.Create(inner.Id, null, P("yml/a.yml"), false, 7, Now, Now, FileAttributes.Normal),
                FileEntry.Create(inner.Id, null, "readme.md", false, 3, Now, Now, FileAttributes.Normal),
            ],
            1,
            TestContext.Current.CancellationToken);
        var yml = (await context.Entries.GetByPathAsync(inner.Id, P("yml/a.yml"), TestContext.Current.CancellationToken))!;

        // Act
        var moved = await context.Entries.MoveSubtreeAsync(inner.Id, string.Empty, context.Root.Id, P("10S-417/TCI2"), TestContext.Current.CancellationToken);

        // Assert
        moved.ShouldBe(3);
        var placeholder = await context.GetAsync("10S-417/TCI2");
        placeholder.IsDirectory.ShouldBeTrue();
        placeholder.Name.ShouldBe("TCI2");
        placeholder.ParentId.ShouldBe(order.Id);
        (await context.GetAsync("10S-417/TCI2/readme.md")).ParentId.ShouldBe(placeholder.Id);
        var movedFolder = await context.GetAsync("10S-417/TCI2/yml");
        movedFolder.ParentId.ShouldBe(placeholder.Id);
        var movedYml = await context.GetAsync("10S-417/TCI2/yml/a.yml");
        movedYml.Id.ShouldBe(yml.Id);
        movedYml.ParentId.ShouldBe(movedFolder.Id);
        movedYml.Depth.ShouldBe(4);
        (await context.Entries.GetStatsAsync(inner.Id, TestContext.Current.CancellationToken)).FileCount.ShouldBe(0);
        (await context.Entries.GetChildrenOfPathAsync(context.Root.Id, P("10S-417/TCI2"), TestContext.Current.CancellationToken)).Select(static entry => entry.Name).Order().ShouldBe(["readme.md", "yml"]);
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

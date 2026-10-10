using DiskApp.Core.Domain;
using DiskApp.Core.Indexing;
using DiskApp.Core.Indexing.Watching;
using DiskApp.Core.Paths;
using DiskApp.Infrastructure.FileSystem;

namespace DiskApp.Infrastructure.Tests.Indexing;

/// <summary>
/// Применение склеенных изменений: создание, изменение, удаление, переименование файла и папки с сохранением
/// идентификатора, замена файла папкой, исключения, родитель вне индекса, новые папки, номер сканирования.
/// </summary>
public sealed class ChangeApplierTests
{
    [Fact]
    public async Task ApplyAsync_CreatedModifiedDeleted_IndexMatchesDisk()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        context.Files.CreateFile(Path.Combine("docs", "keep.txt"), "k");
        context.Files.CreateFile(Path.Combine("docs", "gone.txt"), "g");
        await context.FullScanAsync();
        context.Files.CreateFile(Path.Combine("docs", "new.txt"), "n");
        File.AppendAllText(context.Files.Resolve(Path.Combine("docs", "keep.txt")), "more");
        File.Delete(context.Files.Resolve(Path.Combine("docs", "gone.txt")));

        // Act
        var result = await ApplyAsync(context, Paths(Path.Combine("docs", "new.txt"), Path.Combine("docs", "keep.txt"), Path.Combine("docs", "gone.txt"), "docs"));

        // Assert
        result.Written.Inserted.ShouldBe(1);
        result.Written.Updated.ShouldBeGreaterThanOrEqualTo(1);
        result.Removed.ShouldBe(1);
        context.AssertIndexMatchesDisk();
        context.Changes.SelectMany(static change => change.Changes).Select(static change => change.RelPath).ShouldContain("docs");
    }

    [Fact]
    public async Task ApplyAsync_RenameFolder_KeepsIdsOfSubtree()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        context.Files.CreateFile(Path.Combine("old", "sub", "file.txt"), "f");
        await context.FullScanAsync();
        var before = await context.Entries.GetByPathAsync(context.Root.Id, Path.Combine("old", "sub", "file.txt"), TestContext.Current.CancellationToken);
        Directory.Move(context.Files.Resolve("old"), context.Files.Resolve("new"));

        // Act
        var result = await ApplyAsync(context, new CoalescedChanges([("old", "new")], ["old", "new"], false));

        // Assert
        result.Renamed.ShouldBe(3);
        var after = await context.Entries.GetByPathAsync(context.Root.Id, Path.Combine("new", "sub", "file.txt"), TestContext.Current.CancellationToken);
        after.ShouldNotBeNull();
        after.Id.ShouldBe(before!.Id);
        context.AssertIndexMatchesDisk();
    }

    [Fact]
    public async Task ApplyAsync_RenameIntoExcludedOrHiddenParent_RemovesEntry()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        context.Files.CreateFile("report.pdf", "r");
        context.Files.CreateFile("photo.jpg", "p");
        var hidden = context.Files.CreateDirectory("hidden");
        File.SetAttributes(hidden, FileAttributes.Directory | FileAttributes.Hidden);
        await context.FullScanAsync();
        File.Move(context.Files.Resolve("report.pdf"), context.Files.Resolve("report.tmp"));
        File.Move(context.Files.Resolve("photo.jpg"), context.Files.Resolve(Path.Combine("hidden", "photo.jpg")));

        // Act
        await ApplyAsync(
            context,
            new CoalescedChanges([("report.pdf", "report.tmp"), ("photo.jpg", Path.Combine("hidden", "photo.jpg"))], ["report.pdf", "report.tmp", "photo.jpg", Path.Combine("hidden", "photo.jpg")], false),
            ExclusionRule.Defaults);

        // Assert
        context.IndexSnapshot().ShouldBeEmpty("скрытая папка не индексируется, *.tmp исключён");
    }

    [Fact]
    public async Task ApplyAsync_RenameFromTemporaryName_CreatesEntry()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        await context.FullScanAsync();
        context.Files.CreateFile("setup.exe.crdownload", "partial");
        File.Move(context.Files.Resolve("setup.exe.crdownload"), context.Files.Resolve("setup.exe"));

        // Act
        await ApplyAsync(context, new CoalescedChanges([("setup.exe.crdownload", "setup.exe")], ["setup.exe.crdownload", "setup.exe"], false), ExclusionRule.Defaults);

        // Assert
        var entry = await context.Entries.GetByPathAsync(context.Root.Id, "setup.exe", TestContext.Current.CancellationToken);
        entry.ShouldNotBeNull();
        entry.Kind.ShouldBe(FileKind.Executable);
    }

    [Fact]
    public async Task ApplyAsync_FileReplacedByFolder_ReplacesEntry()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        context.Files.CreateFile("thing", "file");
        await context.FullScanAsync();
        File.Delete(context.Files.Resolve("thing"));
        context.Files.CreateDirectory("thing");

        // Act
        var result = await ApplyAsync(context, Paths("thing"));

        // Assert
        result.NewDirectories.ShouldBe(["thing"]);
        (await context.Entries.GetByPathAsync(context.Root.Id, "thing", TestContext.Current.CancellationToken))!.IsDirectory.ShouldBeTrue();
    }

    [Fact]
    public async Task ApplyAsync_NewNestedFolders_ReportsOnlyOutermost()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        await context.FullScanAsync();
        context.Files.CreateFile(Path.Combine("a", "b", "c", "deep.txt"), "d");

        // Act
        var result = await ApplyAsync(context, Paths("a", Path.Combine("a", "b"), Path.Combine("a", "b", "c"), Path.Combine("a", "b", "c", "deep.txt")));

        // Assert
        result.NewDirectories.ShouldBe(["a"]);
        result.Written.Inserted.ShouldBe(4);
        context.AssertIndexMatchesDisk();
    }

    [Fact]
    public async Task ApplyAsync_ParentNotIndexed_SkipsEntry()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        context.Files.CreateFile(Path.Combine("node_modules", "lib", "index.js"), "x");
        context.Files.CreateFile(Path.Combine("unscanned", "file.txt"), "y");

        // Act: корень не сканировался, родителей в индексе нет
        var result = await ApplyAsync(context, Paths(Path.Combine("unscanned", "file.txt"), Path.Combine("node_modules", "lib", "index.js")), ExclusionRule.Defaults);

        // Assert
        result.Skipped.ShouldBe(1);
        result.Written.Total.ShouldBe(0);
        context.IndexSnapshot().ShouldBeEmpty();
    }

    [Fact]
    public async Task ApplyAsync_DuringActiveScan_UsesScanIdAndRecordsTouched()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        await context.FullScanAsync();
        context.Files.CreateFile("during.txt", "d");
        var state = new RootIndexState(PathNormalizer.Current);
        state.BeginScan(42);

        // Act
        await ApplyAsync(context, Paths("during.txt"), state: state);

        // Assert
        var swept = await context.Entries.SweepAsync(context.Root.Id, 42, TestContext.Current.CancellationToken);
        swept.ShouldBe(0, "запись наблюдателя помечена номером идущего сканирования");
        (await context.Entries.GetByPathAsync(context.Root.Id, "during.txt", TestContext.Current.CancellationToken)).ShouldNotBeNull();
        state.EndScan().ShouldBe(["during.txt"]);
    }

    [Fact]
    public async Task ApplyAsync_UnchangedPath_WritesNothing()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        context.Files.CreateFile("same.txt", "s");
        await context.FullScanAsync();
        context.Changes.Clear();

        // Act
        var result = await ApplyAsync(context, Paths("same.txt"));

        // Assert
        result.Written.Total.ShouldBe(0);
        context.Changes.ShouldBeEmpty();
    }

    private static CoalescedChanges Paths(params string[] relPaths) => new([], relPaths, false);

    private static Task<ApplyResult> ApplyAsync(IndexingTestContext context, CoalescedChanges changes, IEnumerable<ExclusionRule>? rules = null, RootIndexState? state = null)
    {
        var applier = new ChangeApplier(FileSystemLister.Instance, context.Entries, context.Events, context.Factory);
        var matcher = ExclusionMatcher.Create(rules ?? [], context.Root, PathNormalizer.Current);
        return applier.ApplyAsync(context.Root, changes, matcher, state ?? new RootIndexState(PathNormalizer.Current), TestContext.Current.CancellationToken);
    }
}

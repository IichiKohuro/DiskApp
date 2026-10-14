using DiskApp.Core.Domain;
using DiskApp.Core.Events;
using DiskApp.Core.Indexing;

namespace DiskApp.Infrastructure.Tests.Indexing;

/// <summary>
/// Сверка по разнице: без изменений ничего не пишет, ловит добавления, изменения, удаления, замену файла папкой,
/// смену регистра с сохранением идентификатора, исключения, поддерево и недоступный корень.
/// </summary>
public sealed class ReconcileRunnerTests
{
    [Fact]
    public async Task RunAsync_MovedWhileOffline_JournalsRemovedAndAddedTopLevel_UnlessServiceReconcile()
    {
        // Arrange: история файлов (этап 20) — пока DiskApp не работал, файл перенесли, а папку с файлами принесли
        await using var context = await IndexingTestContext.CreateAsync();
        context.Files.CreateFile(Path.Combine("a", "report.pdf"), "report");
        context.Files.CreateDirectory("b");
        await context.FullScanAsync();
        File.Move(context.Files.Resolve(Path.Combine("a", "report.pdf")), context.Files.Resolve(Path.Combine("b", "report.pdf")));
        context.Files.CreateFile(Path.Combine("c", "one.txt"), "1");
        context.Files.CreateFile(Path.Combine("c", "two.txt"), "2");
        var journal = new List<IndexJournalEvent>();
        using var subscription = context.Events.Subscribe<IndexJournalEvent>(journal.Add);

        // Act
        await context.ReconcileAsync();
        var first = journal.ToArray();
        journal.Clear();
        File.Move(context.Files.Resolve(Path.Combine("b", "report.pdf")), context.Files.Resolve(Path.Combine("a", "report.pdf")));
        await context.Reconcile.RunAsync(context.Request() with { Journal = false }, PauseToken.None, TestContext.Current.CancellationToken);

        // Assert: содержимое новой папки — часть её появления; служебная сверка в историю не пишет
        var changes = first.ShouldHaveSingleItem().Changes;
        first[0].Source.ShouldBe(JournalSource.Reconcile);
        changes.Select(static change => (change.Kind, change.Path)).ShouldBe(
        [
            (JournalChangeKind.Removed, context.Files.Resolve(Path.Combine("a", "report.pdf"))),
            (JournalChangeKind.Added, context.Files.Resolve(Path.Combine("b", "report.pdf"))),
            (JournalChangeKind.Added, context.Files.Resolve("c")),
        ], ignoreOrder: true);
        journal.ShouldBeEmpty();
        context.AssertIndexMatchesDisk();
    }

    [Fact]
    public async Task RunAsync_NothingChanged_WritesNothing()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        var created = TestTree.Generate(context.Files, files: 150, folders: 3, depth: 3);
        await context.FullScanAsync();
        context.Changes.Clear();

        // Act
        var outcome = await context.ReconcileAsync();

        // Assert
        outcome.Kind.ShouldBe(IndexTaskKind.Reconcile);
        outcome.Status.ShouldBe(ScanOutcomeStatus.Completed);
        outcome.Written.Total.ShouldBe(0);
        outcome.Removed.ShouldBe(0);
        outcome.Found.ShouldBe(created.Count);
        context.Changes.ShouldBeEmpty();
    }

    [Fact]
    public async Task RunAsync_EmptyIndex_BuildsFullIndex()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        var created = TestTree.Generate(context.Files, files: 80, folders: 2, depth: 3);

        // Act
        var outcome = await context.ReconcileAsync();

        // Assert
        outcome.Written.Inserted.ShouldBe(created.Count);
        context.AssertIndexMatchesDisk();
    }

    [Fact]
    public async Task RunAsync_ChangesWhileOffline_IndexMatchesDisk()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        var created = TestTree.Generate(context.Files, files: 90, folders: 3, depth: 2);
        context.Files.CreateFile("will-become-folder", "file");
        await context.FullScanAsync();

        Directory.Delete(context.Files.Resolve("folder001"), recursive: true);
        File.Delete(context.Files.Resolve(FirstFileUnder(created, "folder000")));
        File.AppendAllText(context.Files.Resolve(FirstFileUnder(created, "folder002")), "grown");
        context.Files.CreateFile(Path.Combine("folder000", "fresh", "deeper", "new.pdf"), "new");
        File.Delete(context.Files.Resolve("will-become-folder"));
        context.Files.CreateFile(Path.Combine("will-become-folder", "inside.txt"), "now a folder");

        // Act
        var outcome = await context.ReconcileAsync();

        // Assert
        outcome.Removed.ShouldBeGreaterThan(20);
        outcome.Written.Inserted.ShouldBeGreaterThanOrEqualTo(5);
        context.AssertIndexMatchesDisk();
    }

    [Fact]
    public async Task RunAsync_CaseOnlyRename_KeepsEntryId()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Смена регистра без смены пути имеет смысл только в Windows.");
        }

        context.Files.CreateFile(Path.Combine("docs", "report.txt"), "r");
        await context.FullScanAsync();
        var before = await context.Entries.GetByPathAsync(context.Root.Id, Path.Combine("docs", "report.txt"), TestContext.Current.CancellationToken);
        Directory.Move(context.Files.Resolve("docs"), context.Files.Resolve("docs-tmp"));
        Directory.Move(context.Files.Resolve("docs-tmp"), context.Files.Resolve("Docs"));

        // Act
        await context.ReconcileAsync();

        // Assert
        var after = await context.Entries.GetByPathAsync(context.Root.Id, Path.Combine("Docs", "report.txt"), TestContext.Current.CancellationToken);
        after.ShouldNotBeNull();
        after.Id.ShouldBe(before!.Id);
        context.AssertIndexMatchesDisk();
    }

    [Fact]
    public async Task RunAsync_NewExclusionRule_RemovesExcludedEntries()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        context.Files.CreateFile(Path.Combine("app", "node_modules", "lib.js"), "x");
        context.Files.CreateFile(Path.Combine("app", "main.cs"), "y");
        await context.FullScanAsync();

        // Act
        var outcome = await context.ReconcileAsync(rules: ExclusionRule.Defaults);

        // Assert
        outcome.Removed.ShouldBe(2);
        context.IndexSnapshot().Keys.ShouldBe(["app", Path.Combine("app", "main.cs")], ignoreOrder: true);
    }

    [Fact]
    public async Task RunAsync_Subtree_TouchesOnlySubtree()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        context.Files.CreateFile(Path.Combine("docs", "a.txt"), "a");
        context.Files.CreateFile(Path.Combine("other", "b.txt"), "b");
        await context.FullScanAsync();
        context.Files.CreateFile(Path.Combine("docs", "c.txt"), "c");
        context.Files.CreateFile(Path.Combine("other", "d.txt"), "d");

        // Act
        var outcome = await context.ReconcileAsync("docs");

        // Assert
        outcome.Written.Inserted.ShouldBe(1);
        var index = context.IndexSnapshot();
        index.Keys.ShouldContain(Path.Combine("docs", "c.txt"));
        index.Keys.ShouldNotContain(Path.Combine("other", "d.txt"));
    }

    [Fact]
    public async Task RunAsync_NewSubtree_InsertsFolderAndContent()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        context.Files.CreateDirectory("existing");
        await context.FullScanAsync();
        context.Files.CreateFile(Path.Combine("existing", "moved-in", "a.txt"), "a");
        context.Files.CreateFile(Path.Combine("existing", "moved-in", "sub", "b.txt"), "b");

        // Act
        var outcome = await context.ReconcileAsync(Path.Combine("existing", "moved-in"));

        // Assert: родитель вне поддерева не сверяется — его обновит событие наблюдателя или сверка корня
        outcome.Written.Inserted.ShouldBe(4);
        context.IndexSnapshot().Keys.ShouldBe(
            ["existing", Path.Combine("existing", "moved-in"), Path.Combine("existing", "moved-in", "a.txt"), Path.Combine("existing", "moved-in", "sub"), Path.Combine("existing", "moved-in", "sub", "b.txt")],
            ignoreOrder: true);
        context.AssertParentsConsistent();
    }

    [Fact]
    public async Task RunAsync_MissingSubtreeOrRoot_RemovesSubtreeOrKeepsIndex()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        context.Files.CreateFile(Path.Combine("gone", "a.txt"), "a");
        context.Files.CreateFile("keep.txt", "k");
        await context.FullScanAsync();
        Directory.Delete(context.Files.Resolve("gone"), recursive: true);
        var offline = context.Request() with { Root = context.Root with { Path = context.Files.Resolve("unplugged") } };

        // Act
        var subtree = await context.ReconcileAsync("gone");
        var root = await context.Reconcile.RunAsync(offline, PauseToken.None, TestContext.Current.CancellationToken);

        // Assert
        subtree.Status.ShouldBe(ScanOutcomeStatus.SubtreeMissing);
        subtree.Removed.ShouldBe(2);
        root.Status.ShouldBe(ScanOutcomeStatus.RootMissing);
        context.IndexSnapshot().Keys.ShouldBe(["keep.txt"]);
    }

    [Fact]
    public async Task RunAsync_InaccessibleFolder_KeepsItsEntries()
    {
        // Arrange
        await using var context = await IndexingTestContext.CreateAsync();
        context.Files.CreateFile(Path.Combine("locked", "secret.txt"), "s");
        await context.FullScanAsync();
        using var denied = TestTree.DenyAccess(context.Files, "locked");
        if (denied is null)
        {
            Assert.Skip("Запрет доступа к папке недоступен на этой ОС.");
        }

        // Act
        var outcome = await context.ReconcileAsync();

        // Assert
        outcome.ErrorCount.ShouldBe(1);
        outcome.Removed.ShouldBe(0);
        context.IndexSnapshot().Keys.ShouldContain(Path.Combine("locked", "secret.txt"));
    }

    private static string FirstFileUnder(IReadOnlyList<string> created, string folder)
        => created.First(path => path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal) && Path.HasExtension(path));
}

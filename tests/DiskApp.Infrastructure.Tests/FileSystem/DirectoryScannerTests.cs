using DiskApp.Core.Domain;
using DiskApp.Core.Indexing;

using static DiskApp.Infrastructure.Tests.FileSystem.ScanTestHelpers;

namespace DiskApp.Infrastructure.Tests.FileSystem;

/// <summary>
/// Сканер на реальной файловой системе: полнота, порядок «родитель раньше детей», исключения, глубина,
/// junction и цикл, недоступная папка, длинные и Unicode-пути, поддерево, отмена и пауза.
/// </summary>
public sealed class DirectoryScannerTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task ScanAsync_GeneratedTree_FindsEveryEntryParentsFirst(int parallelism)
    {
        // Arrange
        using var folder = new TemporaryDirectory("scan");
        var expected = TestTree.Generate(folder, files: 300, folders: 6, depth: 3);
        var request = new ScanRequest { Root = RootFor(folder), Matcher = ExclusionMatcher.None, DegreeOfParallelism = parallelism, BatchSize = 37 };

        // Act
        var (summary, entries, batches) = await ScanAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        entries.Select(static entry => entry.RelPath).ShouldBe(expected, ignoreOrder: true);
        summary.Entries.ShouldBe(expected.Count);
        summary.ErrorCount.ShouldBe(0);
        summary.StartMissing.ShouldBeFalse();
        batches.Count.ShouldBeGreaterThan(1);
        AssertParentsFirst(entries);
        var file = entries.First(static entry => entry.Name == "file000001.pdf");
        file.Kind.ShouldBe(FileKind.Pdf);
        file.Size.ShouldBe("file000001.pdf".Length);
        file.RootId.ShouldBe(1);
    }

    [Fact]
    public async Task ScanAsync_DefaultExclusions_SkipsExcludedFoldersAndTheirContent()
    {
        // Arrange
        using var folder = new TemporaryDirectory("scan");
        folder.CreateFile(Path.Combine("app", "Program.cs"));
        folder.CreateFile(Path.Combine("app", "node_modules", "lib", "index.js"));
        folder.CreateFile(Path.Combine("app", "bin", "Debug", "app.dll"));
        folder.CreateFile(Path.Combine("app", "draft.tmp"));
        var root = RootFor(folder);
        var request = new ScanRequest { Root = root, Matcher = ExclusionMatcher.Create(ExclusionRule.Defaults, root, Core.Paths.PathNormalizer.Current) };

        // Act
        var (_, entries, _) = await ScanAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        entries.Select(static entry => entry.RelPath).ShouldBe(["app", Path.Combine("app", "Program.cs")], ignoreOrder: true);
    }

    [Fact]
    public async Task ScanAsync_HiddenFile_SkippedUnlessRootIncludesHidden()
    {
        // Arrange
        using var folder = new TemporaryDirectory("scan");
        var hidden = folder.CreateFile("secret.txt");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        folder.CreateFile("visible.txt");
        var root = RootFor(folder);

        // Act
        var (_, strict, _) = await ScanAsync(
            new ScanRequest { Root = root, Matcher = ExclusionMatcher.Create([], root, Core.Paths.PathNormalizer.Current) },
            cancellationToken: TestContext.Current.CancellationToken);
        var withHidden = root with { IncludeHidden = true };
        var (_, permissive, _) = await ScanAsync(
            new ScanRequest { Root = withHidden, Matcher = ExclusionMatcher.Create([], withHidden, Core.Paths.PathNormalizer.Current) },
            cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        strict.Select(static entry => entry.Name).ShouldBe(["visible.txt"]);
        permissive.Select(static entry => entry.Name).ShouldBe(["secret.txt", "visible.txt"], ignoreOrder: true);
        permissive.Single(static entry => entry.Name == "secret.txt").IsHidden.ShouldBeTrue();
    }

    [Fact]
    public async Task ScanAsync_MaxDepth_StopsDescending()
    {
        // Arrange
        using var folder = new TemporaryDirectory("scan");
        folder.CreateFile(Path.Combine("a", "b", "c", "deep.txt"));
        var root = RootFor(folder) with { MaxDepth = 2 };
        var request = new ScanRequest { Root = root, Matcher = ExclusionMatcher.Create([], root, Core.Paths.PathNormalizer.Current) };

        // Act
        var (_, entries, _) = await ScanAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        entries.Select(static entry => entry.RelPath).ShouldBe(["a", Path.Combine("a", "b")], ignoreOrder: true);
    }

    [Fact]
    public async Task ScanAsync_Junction_IndexedButNotFollowedByDefault()
    {
        // Arrange
        using var folder = new TemporaryDirectory("scan");
        folder.CreateFile(Path.Combine("target", "inside.txt"));
        if (!TestTree.TryCreateJunction(folder, "link", "target"))
        {
            Assert.Skip("Junction недоступны на этой ОС.");
        }

        var request = new ScanRequest { Root = RootFor(folder), Matcher = ExclusionMatcher.None };

        // Act
        var (_, entries, _) = await ScanAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        entries.Select(static entry => entry.RelPath).ShouldBe(["target", Path.Combine("target", "inside.txt"), "link"], ignoreOrder: true);
        entries.Single(static entry => entry.Name == "link").IsReparsePoint.ShouldBeTrue();
    }

    [Fact]
    public async Task ScanAsync_JunctionCycleWithFollowing_Terminates()
    {
        // Arrange
        using var folder = new TemporaryDirectory("scan");
        folder.CreateFile(Path.Combine("a", "b", "file.txt"));
        if (!TestTree.TryCreateJunction(folder, Path.Combine("a", "b", "loop"), "a"))
        {
            Assert.Skip("Junction недоступны на этой ОС.");
        }

        var request = new ScanRequest { Root = RootFor(folder), Matcher = ExclusionMatcher.None, FollowReparsePoints = true, DegreeOfParallelism = 2 };

        // Act
        var (summary, entries, _) = await ScanAsync(request, cancellationToken: TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Assert
        entries.Select(static entry => entry.RelPath).ShouldBe(
            ["a", Path.Combine("a", "b"), Path.Combine("a", "b", "file.txt"), Path.Combine("a", "b", "loop")],
            ignoreOrder: true);
        summary.ErrorCount.ShouldBe(0);
    }

    [Fact]
    public async Task ScanAsync_JunctionToOutsideWithFollowing_IndexesTargetContent()
    {
        // Arrange
        using var folder = new TemporaryDirectory("scan");
        folder.CreateFile(Path.Combine("outside", "shared.txt"));
        folder.CreateDirectory("root");
        if (!TestTree.TryCreateJunction(folder, Path.Combine("root", "link"), "outside"))
        {
            Assert.Skip("Junction недоступны на этой ОС.");
        }

        var root = new Root { Id = 1, Path = folder.Resolve("root"), Label = "root" };
        var request = new ScanRequest { Root = root, Matcher = ExclusionMatcher.None, FollowReparsePoints = true };

        // Act
        var (_, entries, _) = await ScanAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        entries.Select(static entry => entry.RelPath).ShouldBe(["link", Path.Combine("link", "shared.txt")], ignoreOrder: true);
    }

    [Fact]
    public async Task ScanAsync_InaccessibleFolder_ReportedAndScanContinues()
    {
        // Arrange
        using var folder = new TemporaryDirectory("scan");
        folder.CreateFile(Path.Combine("locked", "secret.txt"));
        folder.CreateFile(Path.Combine("open", "visible.txt"));
        using var denied = TestTree.DenyAccess(folder, "locked");
        if (denied is null)
        {
            Assert.Skip("Запрет доступа к папке недоступен на этой ОС.");
        }

        var request = new ScanRequest { Root = RootFor(folder), Matcher = ExclusionMatcher.None };

        // Act
        var (summary, entries, _) = await ScanAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        entries.Select(static entry => entry.RelPath).ShouldBe(["locked", "open", Path.Combine("open", "visible.txt")], ignoreOrder: true);
        summary.ErrorCount.ShouldBe(1);
        summary.FailedDirectories.ShouldBe(["locked"]);
        summary.Errors.ShouldHaveSingleItem().RelPath.ShouldBe("locked");
    }

    [Fact]
    public async Task ScanAsync_LongAndUnicodePaths_AreIndexed()
    {
        // Arrange
        using var folder = new TemporaryDirectory("scan");
        var longFile = TestTree.CreateLongPath(folder);
        var unicode = TestTree.CreateUnicodeNames(folder);
        var request = new ScanRequest { Root = RootFor(folder), Matcher = ExclusionMatcher.None };

        // Act
        var (summary, entries, _) = await ScanAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        summary.ErrorCount.ShouldBe(0);
        Path.Combine(folder.FullPath, longFile).Length.ShouldBeGreaterThan(260);
        entries.Select(static entry => entry.RelPath).ShouldContain(longFile);
        foreach (var path in unicode)
        {
            entries.Select(static entry => entry.RelPath).ShouldContain(path);
        }

        entries.Single(static entry => entry.Name == "Отчёт за квартал.docx").Kind.ShouldBe(FileKind.Document);
    }

    [Fact]
    public async Task ScanAsync_Subtree_EmitsStartFolderAndItsContentOnly()
    {
        // Arrange
        using var folder = new TemporaryDirectory("scan");
        folder.CreateFile(Path.Combine("docs", "a.txt"));
        folder.CreateFile(Path.Combine("docs", "sub", "b.txt"));
        folder.CreateFile(Path.Combine("other", "c.txt"));
        var request = new ScanRequest { Root = RootFor(folder), RelPath = "docs", Matcher = ExclusionMatcher.None };

        // Act
        var (summary, entries, _) = await ScanAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        entries[0].RelPath.ShouldBe("docs");
        entries.Select(static entry => entry.RelPath).ShouldBe(
            ["docs", Path.Combine("docs", "a.txt"), Path.Combine("docs", "sub"), Path.Combine("docs", "sub", "b.txt")],
            ignoreOrder: true);
        summary.Entries.ShouldBe(4);
    }

    [Fact]
    public async Task ScanAsync_MissingRootOrSubtree_ReportsStartMissing()
    {
        // Arrange
        using var folder = new TemporaryDirectory("scan");
        var missingRoot = new Root { Id = 1, Path = folder.Resolve("nope"), Label = "nope" };

        // Act
        var (rootSummary, rootEntries, _) = await ScanAsync(
            new ScanRequest { Root = missingRoot, Matcher = ExclusionMatcher.None }, cancellationToken: TestContext.Current.CancellationToken);
        var (subtreeSummary, _, _) = await ScanAsync(
            new ScanRequest { Root = RootFor(folder), RelPath = "gone", Matcher = ExclusionMatcher.None }, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        rootSummary.StartMissing.ShouldBeTrue();
        rootEntries.ShouldBeEmpty();
        subtreeSummary.StartMissing.ShouldBeTrue();
    }

    [Fact]
    public async Task ScanAsync_ExcludedSubtree_ReportsStartExcluded()
    {
        // Arrange
        using var folder = new TemporaryDirectory("scan");
        folder.CreateFile(Path.Combine("node_modules", "x.js"));
        var root = RootFor(folder);
        var request = new ScanRequest { Root = root, RelPath = "node_modules", Matcher = ExclusionMatcher.Create(ExclusionRule.Defaults, root, Core.Paths.PathNormalizer.Current) };

        // Act
        var (summary, entries, _) = await ScanAsync(request, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        summary.StartExcluded.ShouldBeTrue();
        entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task ScanAsync_Cancelled_Throws()
    {
        // Arrange
        using var folder = new TemporaryDirectory("scan");
        TestTree.Generate(folder, files: 50, folders: 3, depth: 2);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var request = new ScanRequest { Root = RootFor(folder), Matcher = ExclusionMatcher.None };

        // Act & Assert
        await Should.ThrowAsync<OperationCanceledException>(() => ScanAsync(request, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task ScanAsync_Paused_WaitsUntilResumed()
    {
        // Arrange
        using var folder = new TemporaryDirectory("scan");
        TestTree.Generate(folder, files: 20, folders: 2, depth: 2);
        var pause = new PauseTokenSource();
        pause.Pause();
        var request = new ScanRequest { Root = RootFor(folder), Matcher = ExclusionMatcher.None };

        // Act
        var scan = ScanAsync(request, pause.Token, TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        var finishedWhilePaused = scan.IsCompleted;
        pause.Resume();
        var (summary, entries, _) = await scan.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Assert
        finishedWhilePaused.ShouldBeFalse();
        summary.Entries.ShouldBe(24);
        entries.Count.ShouldBe(24);
    }

    private static void AssertParentsFirst(List<FileEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var separator = entry.RelPath.LastIndexOf(Path.DirectorySeparatorChar);
            if (separator > 0)
            {
                seen.ShouldContain(entry.RelPath[..separator], $"родитель «{entry.RelPath[..separator]}» должен идти раньше «{entry.RelPath}»");
            }

            seen.Add(entry.RelPath);
        }
    }
}

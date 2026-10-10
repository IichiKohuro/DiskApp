using DiskApp.Core.Domain;
using DiskApp.Core.Indexing;
using DiskApp.Core.Paths;

namespace DiskApp.Core.Tests.Indexing;

/// <summary>
/// Исключения из индекса: glob по имени и пути, стандартный набор, скрытые и системные, размер, глубина, проверка пути
/// для событий наблюдателя.
/// </summary>
public sealed class ExclusionMatcherTests
{
    private static readonly Root DefaultRoot = new() { Id = 1, Path = @"D:\Projects", Label = "Projects" };

    [Theory]
    [InlineData("node_modules", true)]
    [InlineData("NODE_MODULES", true)]
    [InlineData(".git", true)]
    [InlineData("report.tmp", true)]
    [InlineData("~$Отчёт.docx", true)]
    [InlineData("setup.exe.crdownload", true)]
    [InlineData("movie.mkv.part", true)]
    [InlineData("$RECYCLE.BIN", true)]
    [InlineData("report.pdf", false)]
    [InlineData("binary.dat", false)]
    [InlineData("objects", false)]
    public void IsExcluded_DefaultRules_MatchesServiceFoldersAndTemporaryFiles(string name, bool expected)
    {
        // Arrange
        var matcher = ExclusionMatcher.Create(ExclusionRule.Defaults, DefaultRoot, PathNormalizer.Windows);

        // Act
        var excluded = matcher.IsExcluded(name, name, isDirectory: false, FileAttributes.Normal, 10, 1);

        // Assert
        excluded.ShouldBe(expected);
    }

    [Theory]
    [InlineData("docs/*.log", @"docs\app.log", true)]
    [InlineData("docs/*.log", @"docs\sub\app.log", false)]
    [InlineData("docs/**/*.log", @"docs\sub\deep\app.log", true)]
    [InlineData("**/cache", @"a\b\cache", true)]
    [InlineData("**/cache", "cache", true)]
    [InlineData("data/??.csv", @"data\01.csv", true)]
    [InlineData("data/??.csv", @"data\001.csv", false)]
    public void IsExcluded_PathPattern_MatchesRelativePath(string pattern, string relPath, bool expected)
    {
        // Arrange
        var matcher = ExclusionMatcher.Create([ExclusionRule.Glob(pattern)], DefaultRoot, PathNormalizer.Windows);
        var name = relPath[(relPath.LastIndexOf('\\') + 1)..];

        // Act
        var excluded = matcher.IsExcluded(name, relPath, false, FileAttributes.Normal, 1, 2);

        // Assert
        excluded.ShouldBe(expected);
    }

    [Fact]
    public void IsExcluded_HiddenAndSystem_DependOnRootSettingsAndRules()
    {
        // Arrange
        var strict = ExclusionMatcher.Create([], DefaultRoot, PathNormalizer.Windows);
        var permissive = ExclusionMatcher.Create([], DefaultRoot with { IncludeHidden = true, IncludeSystem = true }, PathNormalizer.Windows);
        var forcedHidden = ExclusionMatcher.Create([ExclusionRule.Hidden()], DefaultRoot with { IncludeHidden = true }, PathNormalizer.Windows);

        // Act & Assert
        strict.IsExcluded("a", "a", false, FileAttributes.Hidden, 1, 1).ShouldBeTrue();
        strict.IsExcluded("a", "a", false, FileAttributes.System, 1, 1).ShouldBeTrue();
        permissive.IsExcluded("a", "a", false, FileAttributes.Hidden | FileAttributes.System, 1, 1).ShouldBeFalse();
        forcedHidden.IsExcluded("a", "a", false, FileAttributes.Hidden, 1, 1).ShouldBeTrue();
    }

    [Fact]
    public void IsExcluded_SizeRule_AppliesToFilesOnly()
    {
        // Arrange
        var matcher = ExclusionMatcher.Create([ExclusionRule.MaxSize(1_000)], DefaultRoot, PathNormalizer.Windows);

        // Act & Assert
        matcher.IsExcluded("big.iso", "big.iso", false, FileAttributes.Normal, 1_001, 1).ShouldBeTrue();
        matcher.IsExcluded("ok.iso", "ok.iso", false, FileAttributes.Normal, 1_000, 1).ShouldBeFalse();
        matcher.IsExcluded("folder", "folder", true, FileAttributes.Directory, 5_000, 1).ShouldBeFalse();
    }

    [Fact]
    public void Depth_RootMaxDepthAndRule_TakeMinimum()
    {
        // Arrange
        var matcher = ExclusionMatcher.Create([ExclusionRule.MaxDepth(3)], DefaultRoot with { MaxDepth = 2 }, PathNormalizer.Windows);

        // Act & Assert
        matcher.MaxDepth.ShouldBe(2);
        matcher.IsExcluded("a", @"x\a", false, FileAttributes.Normal, 1, 2).ShouldBeFalse();
        matcher.IsExcluded("a", @"x\y\a", false, FileAttributes.Normal, 1, 3).ShouldBeTrue();
        matcher.CanDescend(1).ShouldBeTrue();
        matcher.CanDescend(2).ShouldBeFalse();
    }

    [Theory]
    [InlineData(@"src\app\Program.cs", false)]
    [InlineData(@"src\node_modules\lib\index.js", true)]
    [InlineData(@"node_modules", true)]
    [InlineData(@"src\bin\Debug\app.dll", true)]
    [InlineData(@"docs\draft.tmp", true)]
    [InlineData(@"docs\Downloads\file.part", true)]
    public void IsPathExcluded_AncestorOrLeafExcluded_ReturnsTrue(string relPath, bool expected)
    {
        // Arrange
        var matcher = ExclusionMatcher.Create(ExclusionRule.Defaults, DefaultRoot, PathNormalizer.Windows);

        // Act
        var excluded = matcher.IsPathExcluded(relPath);

        // Assert
        excluded.ShouldBe(expected);
    }

    [Fact]
    public void IsPathExcluded_BeyondMaxDepth_ReturnsTrue()
    {
        // Arrange
        var matcher = ExclusionMatcher.Create([], DefaultRoot with { MaxDepth = 2 }, PathNormalizer.Windows);

        // Act & Assert
        matcher.IsPathExcluded(@"a\b").ShouldBeFalse();
        matcher.IsPathExcluded(@"a\b\c").ShouldBeTrue();
    }

    [Fact]
    public void None_ExcludesNothing()
    {
        // Act & Assert
        ExclusionMatcher.None.IsExcluded(".git", ".git", true, FileAttributes.Hidden | FileAttributes.System, long.MaxValue, 1000).ShouldBeFalse();
        ExclusionMatcher.None.IsPathExcluded(@"node_modules\x").ShouldBeFalse();
    }

    [Theory]
    [InlineData("*.TMP", "report.tmp", true, true)]
    [InlineData("*.TMP", "report.tmp", false, false)]
    [InlineData("draft*", "Draft-1.docx", true, true)]
    [InlineData("a?c", "abc", true, true)]
    [InlineData("a?c", "a/c", true, false)]
    [InlineData("exact", "exactly", true, false)]
    public void GlobPattern_NamePatterns_RespectCaseMode(string pattern, string name, bool ignoreCase, bool expected)
    {
        // Arrange
        var glob = new GlobPattern(pattern, ignoreCase);

        // Act & Assert
        glob.IsMatch(name, name).ShouldBe(expected);
        glob.MatchesPath.ShouldBeFalse();
        glob.ToString().ShouldBe(pattern);
    }
}

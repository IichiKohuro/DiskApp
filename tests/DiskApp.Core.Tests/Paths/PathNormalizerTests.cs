using DiskApp.Core.Paths;

namespace DiskApp.Core.Tests.Paths;

/// <summary>
/// Нормализация путей: буква диска, разделители, точки, UNC, префикс <c>\\?\</c>, относительные пути и сравнение без регистра.
/// Правила задаются явно (<see cref="PathNormalizer.Windows"/>), поэтому тесты одинаковы на любой ОС.
/// </summary>
public sealed class PathNormalizerTests
{
    [Theory]
    [InlineData(@"c:\projects\diskapp", @"C:\projects\diskapp")]
    [InlineData(@"C:/Projects/DiskApp/", @"C:\Projects\DiskApp")]
    [InlineData(@"C:\\Projects\\\DiskApp", @"C:\Projects\DiskApp")]
    [InlineData(@"  D:\Data\  ", @"D:\Data")]
    [InlineData(@"C:\Projects\.\DiskApp\..\Other", @"C:\Projects\Other")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"c:/", @"C:\")]
    [InlineData(@"C:\Projects\..", @"C:\")]
    [InlineData(@"\\?\C:\Very\Long", @"C:\Very\Long")]
    [InlineData(@"\\server\share", @"\\server\share")]
    [InlineData(@"\\server\share\", @"\\server\share")]
    [InlineData(@"//server/share/folder/", @"\\server\share\folder")]
    [InlineData(@"\\?\UNC\server\share\folder", @"\\server\share\folder")]
    [InlineData(@"C:\Отчёты\2026 год\Январь", @"C:\Отчёты\2026 год\Январь")]
    public void TryNormalize_WindowsAbsolutePath_ProducesCanonicalForm(string input, string expected)
    {
        // Act
        var ok = PathNormalizer.Windows.TryNormalize(input, out var normalized);

        // Assert
        ok.ShouldBeTrue();
        normalized.ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"Projects\DiskApp")]
    [InlineData(@"C:")]
    [InlineData(@"C:Projects")]
    [InlineData(@"\Projects")]
    [InlineData(@"\\server")]
    [InlineData(@"C:\..\Outside")]
    [InlineData(@"C:\Projects\bad|name")]
    [InlineData(@"C:\Projects\bad?name")]
    [InlineData("C:\\Projects\\bad\tname")]
    public void TryNormalize_WindowsRelativeOrInvalidPath_ReturnsFalse(string input)
    {
        // Act
        var ok = PathNormalizer.Windows.TryNormalize(input, out var normalized);

        // Assert
        ok.ShouldBeFalse();
        normalized.ShouldBeNull();
    }

    [Theory]
    [InlineData("/home/user/docs/", "/home/user/docs")]
    [InlineData("//home//user", "/home/user")]
    [InlineData("/home/user/../other/./docs", "/home/other/docs")]
    [InlineData("/", "/")]
    [InlineData("/home/..", "/")]
    public void TryNormalize_PosixAbsolutePath_ProducesCanonicalForm(string input, string expected)
    {
        // Act
        var ok = PathNormalizer.Posix.TryNormalize(input, out var normalized);

        // Assert
        ok.ShouldBeTrue();
        normalized.ShouldBe(expected);
    }

    [Theory]
    [InlineData("home/user")]
    [InlineData("/../etc")]
    [InlineData(@"C:\Projects")]
    public void TryNormalize_PosixRelativeOrInvalidPath_ReturnsFalse(string input)
    {
        // Act
        var ok = PathNormalizer.Posix.TryNormalize(input, out _);

        // Assert
        ok.ShouldBeFalse();
    }

    [Fact]
    public void Normalize_RelativePath_ThrowsArgumentException()
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => PathNormalizer.Windows.Normalize(@"Projects\DiskApp"));
    }

    [Theory]
    [InlineData(@"C:\Projects\DiskApp", @"C:\")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"\\server\share\folder\file.txt", @"\\server\share")]
    [InlineData(@"\\server\share", @"\\server\share")]
    public void GetRoot_WindowsPath_ReturnsDriveOrShare(string path, string expected)
    {
        // Act
        var root = PathNormalizer.Windows.GetRoot(path);

        // Assert
        root.ShouldBe(expected);
        PathNormalizer.Windows.IsRoot(path).ShouldBe(path == expected);
    }

    [Theory]
    [InlineData(@"C:\Projects\DiskApp", "DiskApp")]
    [InlineData(@"C:\Projects\DiskApp\README.md", "README.md")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"\\server\share", @"\\server\share")]
    [InlineData(@"\\server\share\docs", "docs")]
    public void GetFileName_WindowsPath_ReturnsLastSegmentOrRoot(string path, string expected)
    {
        // Act
        var name = PathNormalizer.Windows.GetFileName(path);

        // Assert
        name.ShouldBe(expected);
    }

    [Theory]
    [InlineData(@"C:\Projects\DiskApp", @"C:\Projects")]
    [InlineData(@"C:\Projects", @"C:\")]
    [InlineData(@"C:\", null)]
    [InlineData(@"\\server\share\docs", @"\\server\share")]
    [InlineData(@"\\server\share", null)]
    public void GetParent_WindowsPath_ReturnsParentOrNullForRoot(string path, string? expected)
    {
        // Act
        var parent = PathNormalizer.Windows.GetParent(path);

        // Assert
        parent.ShouldBe(expected);
    }

    [Theory]
    [InlineData(@"C:\Projects", @"DiskApp\src\Program.cs", @"C:\Projects\DiskApp\src\Program.cs")]
    [InlineData(@"C:\Projects", "DiskApp/src/Program.cs", @"C:\Projects\DiskApp\src\Program.cs")]
    [InlineData(@"C:\Projects", @"\DiskApp\", @"C:\Projects\DiskApp")]
    [InlineData(@"C:\", "Windows", @"C:\Windows")]
    [InlineData(@"C:\Projects", "", @"C:\Projects")]
    [InlineData(@"\\server\share", "docs", @"\\server\share\docs")]
    public void Combine_RootAndRelative_JoinsWithSingleSeparator(string root, string relative, string expected)
    {
        // Act
        var combined = PathNormalizer.Windows.Combine(root, relative);

        // Assert
        combined.ShouldBe(expected);
    }

    [Theory]
    [InlineData(@"C:\Projects", @"C:\Projects\DiskApp\README.md", @"DiskApp\README.md")]
    [InlineData(@"c:\projects", @"C:\Projects\DiskApp", "DiskApp")]
    [InlineData(@"C:\Projects", @"C:\Projects", "")]
    [InlineData(@"C:\", @"C:\Windows\System32", @"Windows\System32")]
    public void TryGetRelativePath_PathInsideRoot_ReturnsRemainder(string root, string path, string expected)
    {
        // Act
        var ok = PathNormalizer.Windows.TryGetRelativePath(root, path, out var relative);

        // Assert
        ok.ShouldBeTrue();
        relative.ShouldBe(expected);
    }

    [Theory]
    [InlineData(@"C:\Projects", @"C:\ProjectsOld\DiskApp")]
    [InlineData(@"C:\Projects", @"D:\Projects\DiskApp")]
    [InlineData(@"C:\Projects\DiskApp", @"C:\Projects")]
    public void TryGetRelativePath_PathOutsideRoot_ReturnsFalse(string root, string path)
    {
        // Act
        var ok = PathNormalizer.Windows.TryGetRelativePath(root, path, out var relative);

        // Assert
        ok.ShouldBeFalse();
        relative.ShouldBeNull();
    }

    [Fact]
    public void IsNestedIn_SamePath_ReturnsFalse()
    {
        // Act
        var nested = PathNormalizer.Windows.IsNestedIn(@"C:\Projects", @"c:\projects");

        // Assert
        nested.ShouldBeFalse();
    }

    [Fact]
    public void IsNestedIn_ChildPath_ReturnsTrueIgnoringCase()
    {
        // Act
        var nested = PathNormalizer.Windows.IsNestedIn(@"C:\PROJECTS\DiskApp", @"c:\projects");

        // Assert
        nested.ShouldBeTrue();
    }

    [Fact]
    public void ToExtendedLength_LongDrivePath_AddsPrefix()
    {
        // Arrange
        var path = @"C:\" + new string('a', 300);

        // Act
        var extended = PathNormalizer.Windows.ToExtendedLength(path);

        // Assert
        extended.ShouldBe(@"\\?\" + path);
        PathNormalizer.Windows.ToExtendedLength(extended).ShouldBe(extended);
    }

    [Fact]
    public void ToExtendedLength_LongUncPath_UsesUncPrefix()
    {
        // Arrange
        var path = @"\\server\share\" + new string('b', 300);

        // Act
        var extended = PathNormalizer.Windows.ToExtendedLength(path);

        // Assert
        extended.ShouldBe(@"\\?\UNC\server\share\" + new string('b', 300));
    }

    [Fact]
    public void ToExtendedLength_ShortPathOrPosix_ReturnsUnchanged()
    {
        // Arrange
        var longPosix = "/" + new string('c', 300);

        // Act & Assert
        PathNormalizer.Windows.ToExtendedLength(@"C:\Projects").ShouldBe(@"C:\Projects");
        PathNormalizer.Posix.ToExtendedLength(longPosix).ShouldBe(longPosix);
    }

    [Fact]
    public void Comparer_WindowsStyle_IgnoresCase()
    {
        // Arrange
        var set = new HashSet<string>(PathNormalizer.Windows.Comparer) { @"C:\Projects" };

        // Act & Assert
        set.Contains(@"c:\PROJECTS").ShouldBeTrue();
        new HashSet<string>(PathNormalizer.Posix.Comparer) { "/home" }.Contains("/HOME").ShouldBeFalse();
    }
}

namespace DiskApp.Core.Tests.Fixtures;

/// <summary>
/// Проверки фикстуры <see cref="TemporaryDirectory"/> из DiskApp.Testing и заодно образец оформления теста
/// (CONTRIBUTING.md, раздел «Тесты»): имя <c>Method_Scenario_Expected</c>, секции Arrange / Act / Assert,
/// временные файлы только через фикстуру.
/// </summary>
public sealed class TemporaryDirectoryTests
{
    [Fact]
    public void Constructor_Always_CreatesEmptyDirectoryUnderTemp()
    {
        // Arrange
        var tempRoot = Path.GetTempPath();

        // Act
        using var directory = new TemporaryDirectory();

        // Assert
        Directory.Exists(directory.FullPath).ShouldBeTrue();
        directory.FullPath.ShouldStartWith(tempRoot);
        Directory.EnumerateFileSystemEntries(directory.FullPath).ShouldBeEmpty();
    }

    [Fact]
    public void Constructor_TwoInstances_UseDifferentPaths()
    {
        // Arrange & Act
        using var first = new TemporaryDirectory();
        using var second = new TemporaryDirectory();

        // Assert
        first.FullPath.ShouldNotBe(second.FullPath);
    }

    [Fact]
    public void Constructor_WithPrefix_UsesPrefixInFolderName()
    {
        // Arrange & Act
        using var directory = new TemporaryDirectory("scanner");

        // Assert
        Path.GetFileName(directory.FullPath).ShouldStartWith("scanner-");
    }

    [Fact]
    public void CreateFile_NestedRelativePath_CreatesParentsAndWritesContent()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var relativePath = Path.Combine("docs", "2026", "report.txt");

        // Act
        var filePath = directory.CreateFile(relativePath, "hello");

        // Assert
        filePath.ShouldBe(Path.Combine(directory.FullPath, relativePath));
        File.ReadAllText(filePath).ShouldBe("hello");
    }

    [Fact]
    public void CreateFile_BinaryContent_WritesBytesExactly()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        byte[] content = [0x00, 0xFF, 0x10, 0x20];

        // Act
        var filePath = directory.CreateFile("blob.bin", content);

        // Assert
        File.ReadAllBytes(filePath).ShouldBe(content);
    }

    [Fact]
    public void CreateDirectory_RelativePath_CreatesFolderInside()
    {
        // Arrange
        using var directory = new TemporaryDirectory();

        // Act
        var folderPath = directory.CreateDirectory(Path.Combine("a", "b"));

        // Assert
        Directory.Exists(folderPath).ShouldBeTrue();
        folderPath.ShouldBe(Path.Combine(directory.FullPath, "a", "b"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../outside.txt")]
    [InlineData("a/../../outside.txt")]
    public void Resolve_PathOutsideDirectory_ThrowsArgumentException(string relativePath)
    {
        // Arrange
        using var directory = new TemporaryDirectory();

        // Act & Assert
        Should.Throw<ArgumentException>(() => directory.Resolve(relativePath));
    }

    [Fact]
    public void Resolve_AbsolutePath_ThrowsArgumentException()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var absolutePath = Path.Combine(directory.FullPath, "inside.txt");

        // Act & Assert
        Should.Throw<ArgumentException>(() => directory.Resolve(absolutePath));
    }

    [Fact]
    public void Resolve_Dot_ReturnsDirectoryItself()
    {
        // Arrange
        using var directory = new TemporaryDirectory();

        // Act
        var resolved = directory.Resolve(".");

        // Assert
        resolved.ShouldBe(directory.FullPath);
    }

    [Fact]
    public void Dispose_WithReadOnlyFileInSubfolder_DeletesEverything()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        var filePath = directory.CreateFile(Path.Combine("locked", "readonly.txt"), "x");
        File.SetAttributes(filePath, FileAttributes.ReadOnly);

        // Act
        directory.Dispose();

        // Assert
        Directory.Exists(directory.FullPath).ShouldBeFalse();
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        // Arrange
        using var directory = new TemporaryDirectory();
        directory.Dispose();

        // Act & Assert
        Should.NotThrow(directory.Dispose);
    }
}

using DiskApp.UI.Text;

namespace DiskApp.UI.Tests.Text;

/// <summary>
/// Разбор путей как текста: имя, папка, расширение, сокращение середины; разделители Windows и POSIX.
/// </summary>
public sealed class PathTextTests
{
    [Theory]
    [InlineData(@"C:\Users\Иван\Documents\Отчёт.docx", "Отчёт.docx")]
    [InlineData(@"D:\Projects\DiskApp", "DiskApp")]
    [InlineData(@"D:\Projects\DiskApp\", "DiskApp")]
    [InlineData("/home/user/file.txt", "file.txt")]
    [InlineData(@"D:\", @"D:\")]
    [InlineData("file.txt", "file.txt")]
    public void FileName_Path_ReturnsLastSegment(string path, string expected)
    {
        // Act
        var name = PathText.FileName(path);

        // Assert
        name.ShouldBe(expected);
    }

    [Theory]
    [InlineData(@"D:\Projects\DiskApp\README.md", @"D:\Projects\DiskApp")]
    [InlineData(@"D:\file.txt", @"D:\")]
    [InlineData("/home/user/file.txt", "/home/user")]
    [InlineData("file.txt", "")]
    public void DirectoryName_Path_ReturnsParentWithoutTrailingSeparator(string path, string expected)
    {
        // Act
        var directory = PathText.DirectoryName(path);

        // Assert
        directory.ShouldBe(expected);
    }

    [Theory]
    [InlineData("a.TXT", "txt")]
    [InlineData(".gitignore", "")]
    [InlineData("archive.tar.gz", "gz")]
    [InlineData("noext", "")]
    [InlineData("trailing.", "")]
    public void Extension_Name_ReturnsLowercaseWithoutDot(string path, string expected)
    {
        // Act
        var extension = PathText.Extension(path);

        // Assert
        extension.ShouldBe(expected);
    }

    [Fact]
    public void Shorten_PathWithinLimit_ReturnsUnchanged()
    {
        // Arrange
        const string path = @"D:\Projects\DiskApp";

        // Act
        var shortened = PathText.Shorten(path, 60);

        // Assert
        shortened.ShouldBe(path);
    }

    [Fact]
    public void Shorten_LongPath_KeepsRootAndTailSegments()
    {
        // Arrange
        const string path = @"D:\Projects\DiskApp\src\DiskApp.UI\Views\MainView.axaml";

        // Act
        var shortened = PathText.Shorten(path, 30);

        // Assert
        shortened.ShouldBe(@"D:\…\Views\MainView.axaml");
        shortened.Length.ShouldBeLessThanOrEqualTo(30);
    }

    [Fact]
    public void Shorten_SingleLongSegment_TruncatesMiddle()
    {
        // Arrange
        var name = new string('a', 50) + ".txt";

        // Act
        var shortened = PathText.Shorten(name, 20);

        // Assert
        shortened.Length.ShouldBe(20);
        shortened.ShouldContain("…");
        shortened.ShouldEndWith(".txt");
    }

    [Theory]
    [InlineData(@"D:\Docs\a.txt", "b.txt", @"D:\Docs\b.txt")]
    [InlineData("/home/user/a.txt", "b.txt", "/home/user/b.txt")]
    [InlineData(@"D:\Docs\Папка\", "Новая", @"D:\Docs\Новая")]
    [InlineData("a.txt", "b.txt", "b.txt")]
    public void WithFileName_KeepsFolderAndSeparator(string path, string name, string expected)
    {
        // Act & Assert
        PathText.WithFileName(path, name).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Отчёт.docx", 5)]
    [InlineData("archive.tar.gz", 11)]
    [InlineData(".gitignore", 10)]
    [InlineData("README", 6)]
    public void StemLength_SelectsNameWithoutExtension(string name, int expected)
    {
        // Act & Assert
        PathText.StemLength(name).ShouldBe(expected);
    }

    [Theory]
    [InlineData("Отчёт Q3.docx", null)]
    [InlineData(".gitignore", null)]
    [InlineData("  ", "Имя не может быть пустым.")]
    [InlineData("a/b.txt", "Имя не может содержать символы \\ / : * ? \" < > |.")]
    [InlineData("вопрос?.txt", "Имя не может содержать символы \\ / : * ? \" < > |.")]
    [InlineData("name.", "Имя не может заканчиваться точкой или пробелом.")]
    [InlineData("name ", "Имя не может заканчиваться точкой или пробелом.")]
    [InlineData("nul.txt", "«nul.txt» — зарезервированное имя Windows.")]
    [InlineData("COM1", "«COM1» — зарезервированное имя Windows.")]
    public void ValidateFileName_WindowsRules(string name, string? expected)
    {
        // Act & Assert
        PathText.ValidateFileName(name).ShouldBe(expected);
    }
}

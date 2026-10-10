using DiskApp.Core.Domain;
using DiskApp.Core.Paths;

namespace DiskApp.Core.Tests.Domain;

/// <summary>
/// Запись индекса: расширение из имени, создание по относительному пути, полный путь из корня, глубина и атрибуты.
/// </summary>
public sealed class FileEntryTests
{
    private static readonly DateTimeOffset Created = new(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Modified = new(2026, 10, 1, 12, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("report.PDF", "pdf")]
    [InlineData("archive.tar.gz", "gz")]
    [InlineData("README", "")]
    [InlineData(".gitignore", "")]
    [InlineData("name.", "")]
    [InlineData("Отчёт.Docx", "docx")]
    public void ExtensionOf_Name_ReturnsLowercaseWithoutDot(string name, string expected)
    {
        // Act
        var extension = FileEntry.ExtensionOf(name);

        // Assert
        extension.ShouldBe(expected);
    }

    [Fact]
    public void Create_FilePath_DerivesNameExtensionAndKind()
    {
        // Act
        var entry = FileEntry.Create(
            rootId: 7,
            parentId: 42,
            relPath: "Docs/2026/Quarterly Report.XLSX",
            isDirectory: false,
            size: 12_345,
            createdUtc: Created,
            modifiedUtc: Modified,
            attributes: FileAttributes.Archive,
            normalizer: PathNormalizer.Windows);

        // Assert
        entry.RootId.ShouldBe(7);
        entry.ParentId.ShouldBe(42);
        entry.RelPath.ShouldBe(@"Docs\2026\Quarterly Report.XLSX");
        entry.Name.ShouldBe("Quarterly Report.XLSX");
        entry.Extension.ShouldBe("xlsx");
        entry.Kind.ShouldBe(FileKind.Spreadsheet);
        entry.Size.ShouldBe(12_345);
        entry.Depth.ShouldBe(3);
        entry.IsDirectory.ShouldBeFalse();
    }

    [Fact]
    public void Create_Directory_HasFolderKindNoExtensionAndZeroSize()
    {
        // Act
        var entry = FileEntry.Create(1, null, @"\Photos.old\", isDirectory: true, size: 999, Created, Modified, FileAttributes.Directory, normalizer: PathNormalizer.Windows);

        // Assert
        entry.RelPath.ShouldBe("Photos.old");
        entry.Name.ShouldBe("Photos.old");
        entry.Extension.ShouldBe(string.Empty);
        entry.Kind.ShouldBe(FileKind.Folder);
        entry.Size.ShouldBe(0);
        entry.Depth.ShouldBe(1);
        entry.ParentId.ShouldBeNull();
    }

    [Fact]
    public void Create_WithCustomKindMap_UsesOverrides()
    {
        // Arrange
        var kinds = FileKindMap.Default.With([new KeyValuePair<string, FileKind>("csv", FileKind.Document)]);

        // Act
        var entry = FileEntry.Create(1, null, "data.csv", false, 10, Created, Modified, FileAttributes.Normal, kinds, PathNormalizer.Windows);

        // Assert
        entry.Kind.ShouldBe(FileKind.Document);
    }

    [Fact]
    public void GetFullPath_RootAndRelPath_CombinesWithRootSeparatorRules()
    {
        // Arrange
        var root = new Root { Id = 1, Path = @"D:\Projects", Label = "Projects" };
        var entry = FileEntry.Create(1, null, @"DiskApp\README.md", false, 1, Created, Modified, FileAttributes.Normal, normalizer: PathNormalizer.Windows);

        // Act
        var fullPath = entry.GetFullPath(root.Path, PathNormalizer.Windows);
        var driveRootPath = entry.GetFullPath(@"D:\", PathNormalizer.Windows);

        // Assert
        fullPath.ShouldBe(@"D:\Projects\DiskApp\README.md");
        driveRootPath.ShouldBe(@"D:\DiskApp\README.md");
    }

    [Fact]
    public void AttributeFlags_HiddenSystemReparse_ReflectAttributes()
    {
        // Arrange
        var entry = FileEntry.Create(1, null, "secret", true, 0, Created, Modified,
            FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint, normalizer: PathNormalizer.Windows);

        // Act & Assert
        entry.IsHidden.ShouldBeTrue();
        entry.IsSystem.ShouldBeTrue();
        entry.IsReparsePoint.ShouldBeTrue();
    }

    [Fact]
    public void Create_EmptyRelPath_Throws()
    {
        // Act & Assert
        Should.Throw<ArgumentException>(() => FileEntry.Create(1, null, "  ", false, 0, Created, Modified, FileAttributes.Normal));
    }
}

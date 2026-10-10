using DiskApp.Core.Domain;

namespace DiskApp.Core.Tests.Domain;

/// <summary>
/// Классификация расширений: встроенная таблица, регистр и точка не важны, папка всегда папка, переопределения из настроек.
/// </summary>
public sealed class FileKindMapTests
{
    [Theory]
    [InlineData("jpg", FileKind.Image)]
    [InlineData(".PNG", FileKind.Image)]
    [InlineData("heic", FileKind.Image)]
    [InlineData("mp4", FileKind.Video)]
    [InlineData("flac", FileKind.Audio)]
    [InlineData("docx", FileKind.Document)]
    [InlineData("md", FileKind.Document)]
    [InlineData("xlsx", FileKind.Spreadsheet)]
    [InlineData("csv", FileKind.Spreadsheet)]
    [InlineData("pptx", FileKind.Presentation)]
    [InlineData("pdf", FileKind.Pdf)]
    [InlineData("7z", FileKind.Archive)]
    [InlineData("iso", FileKind.Archive)]
    [InlineData("cs", FileKind.Code)]
    [InlineData("json", FileKind.Code)]
    [InlineData("exe", FileKind.Executable)]
    [InlineData("msi", FileKind.Executable)]
    [InlineData("lnk", FileKind.Shortcut)]
    [InlineData("xyz", FileKind.Other)]
    [InlineData("", FileKind.Other)]
    [InlineData(null, FileKind.Other)]
    public void Classify_Extension_ReturnsBuiltInKind(string? extension, FileKind expected)
    {
        // Act
        var kind = FileKindMap.Default.Classify(extension);

        // Assert
        kind.ShouldBe(expected);
    }

    [Fact]
    public void Classify_Directory_IsFolderRegardlessOfExtension()
    {
        // Act
        var kind = FileKindMap.Default.Classify("jpg", isDirectory: true);

        // Assert
        kind.ShouldBe(FileKind.Folder);
    }

    [Fact]
    public void With_Overrides_ReassignAddAndRemoveExtensions()
    {
        // Arrange
        var overrides = new Dictionary<string, FileKind>
        {
            ["csv"] = FileKind.Document,
            [".DWG"] = FileKind.Image,
            ["log"] = FileKind.Other,
        };

        // Act
        var map = FileKindMap.Default.With(overrides);

        // Assert
        map.Classify("csv").ShouldBe(FileKind.Document);
        map.Classify("dwg").ShouldBe(FileKind.Image);
        map.Classify("log").ShouldBe(FileKind.Other);
        map.Contains("log").ShouldBeFalse();
        FileKindMap.Default.Classify("csv").ShouldBe(FileKind.Spreadsheet, "встроенная таблица не меняется");
    }

    [Fact]
    public void ExtensionsOf_Pdf_ListsOnlyPdf()
    {
        // Act
        var extensions = FileKindMap.Default.ExtensionsOf(FileKind.Pdf);

        // Assert
        extensions.ShouldBe(["pdf"]);
        FileKindMap.Default.ExtensionsOf(FileKind.Folder).ShouldBeEmpty();
    }

    [Fact]
    public void Default_BuiltInTable_HasNoDuplicateExtensions()
    {
        // Arrange
        var kinds = Enum.GetValues<FileKind>().Where(static kind => kind is not FileKind.Folder and not FileKind.Other);

        // Act
        var total = kinds.Sum(kind => FileKindMap.Default.ExtensionsOf(kind).Count);

        // Assert
        total.ShouldBe(FileKindMap.Default.Count);
    }
}

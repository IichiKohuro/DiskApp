using DiskApp.Core.Sorting;

namespace DiskApp.Core.Tests.Sorting;

/// <summary>
/// Признаки имени для подсказок рабочей папки (PLAN.md, этап 17.4): регистр, разделители, «ё», числа и даты не мешают,
/// номер проекта остаётся; предлоги и «копия» отбрасываются; слов не больше восьми; расширение — у файла, не у папки.
/// </summary>
public sealed class SortingFeaturesTests
{
    [Theory]
    [InlineData("Акт сверки 417_12102026.pdf", new[] { "акт", "сверки", "417" })]
    [InlineData("10S-417 протокол.docx", new[] { "417", "протокол" })]
    [InlineData("IMG_20261012_135445.jpg", new[] { "img" })]
    [InlineData("Копия Отчёт по ТЗ (2).docx", new[] { "отчет", "тз" })]
    [InlineData("TCI2 scan_0001.pdf", new[] { "tci", "scan" })]
    [InlineData("Счёт №1234 от 12.10.2026 для ООО.pdf", new[] { "счет", "1234", "ооо" })]
    [InlineData("отчет-ОТЧЁТ.otchet.xlsx", new[] { "отчет", "otchet" })]
    public void Words_Name_NormalizedWithoutNumbersAndDates(string name, string[] expected)
    {
        // Act
        var words = SortingFeatures.Words(name, isFolder: false);

        // Assert
        words.ShouldBe(expected);
    }

    [Fact]
    public void Words_Folder_DotDoesNotSeparateExtension()
    {
        // Act
        var words = SortingFeatures.Words("Проект.Мост", isFolder: true);

        // Assert
        words.ShouldBe(["проект", "мост"]);
    }

    [Fact]
    public void Words_LongName_FirstEightOnly()
    {
        // Act
        var words = SortingFeatures.Words("один два три четыре пять шесть семь восемь девять десять.txt", isFolder: false);

        // Assert
        words.Count.ShouldBe(SortingFeatures.MaxWords);
        words[^1].ShouldBe("восемь");
    }

    [Theory]
    [InlineData("Отчёт.PDF", false, "pdf")]
    [InlineData("README", false, "")]
    [InlineData("Архив.2026", true, "")]
    public void Extension_FileOnly_Lowercase(string name, bool isFolder, string expected)
    {
        // Act
        var extension = SortingFeatures.Extension(name, isFolder);

        // Assert
        extension.ShouldBe(expected);
    }
}

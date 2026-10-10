using Avalonia.Media;

using DiskApp.UI.Controls;

namespace DiskApp.UI.Tests.Controls;

/// <summary>
/// Подсветка совпадений: разбиение текста на фрагменты и построение фрагментов в контроле.
/// </summary>
public sealed class HighlightTextBlockTests
{
    [Fact]
    public void Split_SingleWord_MarksEveryOccurrenceCaseInsensitively()
    {
        // Act
        var parts = HighlightTextBlock.Split("Quarterly Report.pdf", "port");

        // Assert
        parts.ShouldBe([("Quarterly Re", false), ("port", true), (".pdf", false)]);
    }

    [Fact]
    public void Split_CyrillicUppercaseQuery_MatchesLowercaseText()
    {
        // Act
        var parts = HighlightTextBlock.Split("Отчёт за квартал", "ОТЧЁТ");

        // Assert
        parts[0].ShouldBe(("Отчёт", true));
        parts[1].ShouldBe((" за квартал", false));
    }

    [Fact]
    public void Split_EmptyHighlight_ReturnsWholeTextUnmarked()
    {
        // Act
        var parts = HighlightTextBlock.Split("abc", "   ");

        // Assert
        parts.ShouldBe([("abc", false)]);
    }

    [Fact]
    public void Split_OverlappingTokens_MergesAdjacentMatches()
    {
        // Act
        var parts = HighlightTextBlock.Split("report report", "rep ort");

        // Assert
        parts.ShouldBe([("report", true), (" ", false), ("report", true)]);
    }

    [Fact]
    public Task Control_WithHighlight_BuildsRunsWithSemiBoldMatches() => HeadlessTest.RunAsync(static () =>
    {
        // Arrange & Act
        var block = new HighlightTextBlock { Text = "Отчёт.docx", Highlight = "отч" };

        // Assert
        block.Runs.Count.ShouldBe(2);
        block.Runs[0].Text.ShouldBe("Отч");
        block.Runs[0].FontWeight.ShouldBe(FontWeight.SemiBold);
        block.Runs[1].Text.ShouldBe("ёт.docx");
        block.Runs[1].FontWeight.ShouldBe(FontWeight.Normal);
    });
}

using DiskApp.Core.Domain;

namespace DiskApp.Core.Tests.Domain;

/// <summary>
/// Заголовок заметки (PLAN.md, этап 23): первая непустая строка без разметки Markdown в начале.
/// </summary>
public sealed class NoteTextTests
{
    [Theory]
    [InlineData("# Звонок\nтекст", "Звонок")]
    [InlineData("\n\n   ### План  \n", "План")]
    [InlineData("- [ ] Купить бумагу", "Купить бумагу")]
    [InlineData("- [x] Сделано", "Сделано")]
    [InlineData("* пункт", "пункт")]
    [InlineData("> цитата", "цитата")]
    [InlineData("Просто текст\r\nвторая строка", "Просто текст")]
    [InlineData("", "")]
    [InlineData("  \n #  \n", "")]
    public void TitleOf_FirstMeaningfulLine(string body, string expected)
    {
        // Act
        var title = NoteText.TitleOf(body);

        // Assert
        title.ShouldBe(expected);
    }

    [Fact]
    public void TitleOf_LongLine_CutToMaxLength()
    {
        // Arrange
        var body = new string('я', NoteText.MaxTitleLength + 50);

        // Act
        var title = NoteText.TitleOf(body);

        // Assert
        title.Length.ShouldBe(NoteText.MaxTitleLength);
    }
}

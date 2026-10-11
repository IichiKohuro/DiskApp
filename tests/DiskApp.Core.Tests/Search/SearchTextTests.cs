using DiskApp.Core.Search;

namespace DiskApp.Core.Tests.Search;

/// <summary>
/// Свёртка для поиска: регистр и «ё» с сохранением позиций; разложенные последовательности Unicode собираются
/// в готовые символы, а подсветка переводится в позиции исходного имени; запрос приводится к составной форме.
/// </summary>
public sealed class SearchTextTests
{
    [Theory]
    [InlineData("Отчёт.DOCX", "отчет.docx")]
    [InlineData("README", "readme")]
    [InlineData("", "")]
    public void Fold_PlainText_LowersAndFoldsYoKeepingPositions(string text, string expected)
    {
        // Act
        var mapped = SearchText.FoldMapped(text);

        // Assert
        mapped.Folded.ShouldBe(expected);
        mapped.Composed.ShouldBe(text);
        mapped.SourceIndex.ShouldBeNull();
    }

    [Theory]
    [InlineData("Cafe\u0301.pdf", "caf\u00E9.pdf")]
    [InlineData("и\u0306ога", "йога")]
    [InlineData("Е\u0308лка", "елка")]
    [InlineData("か\u3099き", "がき")]
    [InlineData("\u1112\u1161\u11AB", "\uD55C")]
    public void Fold_DecomposedText_ComposesIntoPrecomposedCharacters(string text, string expected)
    {
        // Act
        var folded = SearchText.Fold(text);

        // Assert
        folded.ShouldBe(expected);
    }

    [Fact]
    public void FoldMapped_DecomposedName_MapsRangesToSourcePositions()
    {
        // Arrange: «й» записана как «и» + U+0306
        var mapped = SearchText.FoldMapped("И\u0306ога дом");

        // Act
        var letter = mapped.ToSource(new TextRange(0, 1));
        var rest = mapped.ToSource(new TextRange(1, 3));
        var word = mapped.ToSource(new TextRange(5, 3));

        // Assert
        mapped.Folded.ShouldBe("йога дом");
        mapped.Composed.ShouldBe("Йога дом");
        letter.ShouldBe(new TextRange(0, 2));
        rest.ShouldBe(new TextRange(2, 3));
        word.ShouldBe(new TextRange(6, 3));
    }

    [Theory]
    [InlineData("q\u0307\u0323")]
    [InlineData("\u0301начало")]
    [InlineData("\uD800\u0301")]
    public void Fold_MarksWithoutPrecomposedForm_LeftInPlace(string text)
    {
        // Act
        var folded = SearchText.Fold(text);

        // Assert
        folded.ShouldBe(text.ToLowerInvariant());
    }

    [Theory]
    [InlineData("cafe\u0301", "caf\u00E9")]
    [InlineData("caf\u00E9", "caf\u00E9")]
    [InlineData("\uD800x", "\uD800x")]
    public void Compose_Query_ReturnsComposedFormOrInputWhenInvalid(string text, string expected)
    {
        // Act
        var composed = SearchText.Compose(text);

        // Assert
        composed.ShouldBe(expected);
    }
}

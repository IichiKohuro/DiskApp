using DiskApp.UI.Text;

namespace DiskApp.UI.Tests.Text;

/// <summary>
/// Шаблон имени при переносе: подстановки на русском и английском, расширение сохраняется, номер для пачки без
/// прежнего имени, пустые теги не оставляют лишних «_», недопустимые символы заменяются, папки без расширения.
/// </summary>
public sealed class FileNameTemplateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 14, 30, 0, TimeSpan.FromHours(3));

    [Theory]
    [InlineData(null, "ТЗ 417 v3.docx")]
    [InlineData("", "ТЗ 417 v3.docx")]
    [InlineData("{дата}_{имя}", "11102026_ТЗ 417 v3.docx")]
    [InlineData("{date}_{time}_{name}", "11102026_143000_ТЗ 417 v3.docx")]
    [InlineData("{дата}{время}_{имя}", "11102026143000_ТЗ 417 v3.docx")]
    [InlineData("{имя}_{дата}", "ТЗ 417 v3_11102026.docx")]
    [InlineData("{имя}_{дата}{время}", "ТЗ 417 v3_11102026143000.docx")]
    [InlineData("{имя}_{теги}", "ТЗ 417 v3_заказчик_договор.docx")]
    [InlineData("{папка}_{имя}", "417_ТЗ 417 v3.docx")]
    [InlineData("Техзадание", "Техзадание.docx")]
    [InlineData("{ИМЯ} (копия)", "ТЗ 417 v3 (копия).docx")]
    public void Apply_Template_BuildsNameKeepingExtension(string? template, string expected)
    {
        // Act
        var name = FileNameTemplate.Apply(template, Context("ТЗ 417 v3.docx", tags: ["заказчик", "договор"]));

        // Assert
        name.ShouldBe(expected);
    }

    [Fact]
    public void Apply_CustomNameForSeveralFiles_AddsNumber()
    {
        // Act
        var first = FileNameTemplate.Apply("Счёт", Context("a.pdf", number: 1, count: 3));
        var third = FileNameTemplate.Apply("Счёт", Context("b.pdf", number: 3, count: 3));
        var numbered = FileNameTemplate.Apply("Счёт {№}", Context("b.pdf", number: 2, count: 3));

        // Assert
        first.ShouldBe("Счёт (1).pdf");
        third.ShouldBe("Счёт (3).pdf");
        numbered.ShouldBe("Счёт 2.pdf");
    }

    [Fact]
    public void Apply_EmptyTags_LeaveNoDanglingSeparator()
    {
        // Act
        var name = FileNameTemplate.Apply("{имя}_{теги}", Context("отчёт.xlsx"));

        // Assert
        name.ShouldBe("отчёт.xlsx");
    }

    [Fact]
    public void Apply_InvalidCharacters_ReplacedWithUnderscore()
    {
        // Act
        var name = FileNameTemplate.Apply("{имя}_{теги}", Context("a.txt", tags: ["АО \"Ромашка\"", "в/ч"]));

        // Assert
        name.ShouldBe("a_АО_Ромашка_в_ч.txt");
    }

    [Fact]
    public void Apply_Folder_TemplateCoversWholeName()
    {
        // Act
        var name = FileNameTemplate.Apply("{дата}_{имя}", Context("Проект.v2", isFolder: true));

        // Assert
        name.ShouldBe("11102026_Проект.v2");
    }

    [Fact]
    public void Apply_TemplateOfOnlySeparators_FallsBackToOriginalStem()
    {
        // Act
        var name = FileNameTemplate.Apply("{теги}", Context("план.md"));

        // Assert
        name.ShouldBe("план.md");
    }

    [Theory]
    [InlineData("{дата}_{имя}", true)]
    [InlineData("{name}", true)]
    [InlineData("Счёт {№}", false)]
    [InlineData("", false)]
    public void HasNameToken_DetectsOriginalName(string template, bool expected)
        => FileNameTemplate.HasNameToken(template).ShouldBe(expected);

    private static FileNameContext Context(string name, int number = 1, int count = 1, IReadOnlyList<string>? tags = null, bool isFolder = false)
        => new(name, isFolder, number, count, tags ?? [], "417", Now);
}

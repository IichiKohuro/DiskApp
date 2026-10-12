using DiskApp.UI.Text;

namespace DiskApp.UI.Tests.Text;

/// <summary>
/// Панель форматирования заметки: в записи «|» — курсор, «⟨…⟩» — выделение до и после нажатия. Кнопки оборачивают
/// выделение и снимают обёртку, ставят и снимают маркеры строк, заголовок перебирает уровни и чинит «#Текст»;
/// <c>Enter</c> продолжает и заканчивает список.
/// </summary>
public sealed class MarkdownEditingTests
{
    [Theory]
    [InlineData(MarkdownFormat.Bold, "один ⟨два⟩ три", "один **⟨два⟩** три")]
    [InlineData(MarkdownFormat.Bold, "один **⟨два⟩** три", "один ⟨два⟩ три")]
    [InlineData(MarkdownFormat.Bold, "один ⟨**два**⟩ три", "один ⟨два⟩ три")]
    [InlineData(MarkdownFormat.Bold, "один |три", "один **|**три")]
    [InlineData(MarkdownFormat.Bold, "один⟨ два ⟩три", "один **⟨два⟩** три")]
    [InlineData(MarkdownFormat.Italic, "один ⟨два⟩ три", "один *⟨два⟩* три")]
    [InlineData(MarkdownFormat.Italic, "один *⟨два⟩* три", "один ⟨два⟩ три")]
    [InlineData(MarkdownFormat.Italic, "один **⟨два⟩** три", "один ***⟨два⟩*** три")]
    [InlineData(MarkdownFormat.Strikethrough, "⟨старое⟩", "~~⟨старое⟩~~")]
    [InlineData(MarkdownFormat.Code, "вызов ⟨Main()⟩", "вызов `⟨Main()⟩`")]
    public void Wrap_TogglesMarkersAroundSelection(MarkdownFormat format, string before, string after)
        => Check(format, before, after);

    [Theory]
    [InlineData("Cisco|", "# Cisco|")]
    [InlineData("# Cisco|", "## Cisco|")]
    [InlineData("## Cisco|", "### Cisco|")]
    [InlineData("### Cisco|", "Cisco|")]
    [InlineData("#Cisco Secure Client|", "# Cisco Secure Client|")]
    [InlineData("первая\nвто|рая", "первая\n# вто|рая")]
    public void Heading_CyclesLevelsAndFixesMissingSpace(string before, string after)
        => Check(MarkdownFormat.Heading, before, after);

    [Theory]
    [InlineData(MarkdownFormat.BulletList, "моло|ко", "- моло|ко")]
    [InlineData(MarkdownFormat.BulletList, "- моло|ко", "моло|ко")]
    [InlineData(MarkdownFormat.BulletList, "⟨хлеб\nмолоко⟩", "⟨- хлеб\n- молоко⟩")]
    [InlineData(MarkdownFormat.BulletList, "⟨- хлеб\n- молоко⟩", "⟨хлеб\nмолоко⟩")]
    [InlineData(MarkdownFormat.NumberedList, "⟨хлеб\n\nмолоко⟩", "⟨1. хлеб\n\n2. молоко⟩")]
    [InlineData(MarkdownFormat.TaskList, "⟨- хлеб\n- молоко⟩", "⟨- [ ] хлеб\n- [ ] молоко⟩")]
    [InlineData(MarkdownFormat.TaskList, "- [x] сделано|", "сделано|")]
    [InlineData(MarkdownFormat.Quote, "цита|та", "> цита|та")]
    [InlineData(MarkdownFormat.BulletList, "  отступ|", "  - отступ|")]
    [InlineData(MarkdownFormat.BulletList, "|", "- |")]
    public void LinePrefix_SetsReplacesAndRemovesMarkers(MarkdownFormat format, string before, string after)
        => Check(format, before, after);

    [Theory]
    [InlineData("сайт ⟨Avalonia⟩", "сайт [Avalonia](|)")]
    [InlineData("⟨https://avaloniaui.net⟩", "[|](https://avaloniaui.net)")]
    [InlineData(@"папка ⟨D:\Work\417⟩", @"папка [|](D:\Work\417)")]
    [InlineData("текст |", "текст [|]()")]
    public void Link_WrapsTextOrAddress(string before, string after)
        => Check(MarkdownFormat.Link, before, after);

    [Theory]
    [InlineData("абзац|", "абзац\n\n---\n|")]
    [InlineData("абзац\n\n|", "абзац\n\n---\n|")]
    [InlineData("|", "---\n|")]
    public void Divider_AlwaysAfterBlankLine(string before, string after)
        => Check(MarkdownFormat.Divider, before, after);

    [Fact]
    public void Code_SeveralLines_BecomeFencedBlock()
        => Check(MarkdownFormat.Code, "пример:\n⟨a = 1\nb = 2⟩", "пример:\n```\n⟨a = 1\nb = 2⟩\n```");

    [Fact]
    public void CarriageReturnText_KeepsWindowsLineEnds()
    {
        // Arrange: поле ввода Windows пишет «\r\n»
        var (text, start, end) = Parse("⟨хлеб\r\nмолоко⟩");

        // Act
        var edit = MarkdownEditing.Apply(text, start, end, MarkdownFormat.TaskList);

        // Assert
        edit.ApplyTo(text).ShouldBe("- [ ] хлеб\r\n- [ ] молоко");
    }

    [Theory]
    [InlineData("- хлеб|", "- хлеб\n- |")]
    [InlineData("- [x] купил хлеб|", "- [x] купил хлеб\n- [ ] |")]
    [InlineData("1. первое|", "1. первое\n2. |")]
    [InlineData("  - вложенное|", "  - вложенное\n  - |")]
    [InlineData("> цитата|", "> цитата\n> |")]
    [InlineData("- хлеб\n- |", "- хлеб\n|")]
    public void ContinueList_EnterInItem_NextItemOrEndOfList(string before, string after)
    {
        // Arrange
        var (text, caret, _) = Parse(before);

        // Act
        var edit = MarkdownEditing.ContinueList(text, caret).ShouldNotBeNull();

        // Assert
        Render(edit.ApplyTo(text), edit.SelectionStart, edit.SelectionEnd).ShouldBe(after);
    }

    [Theory]
    [InlineData("обычный текст|")]
    [InlineData("-|")]
    [InlineData("|- пункт")]
    public void ContinueList_NotAnItem_LeavesEnterAlone(string before)
    {
        // Arrange
        var (text, caret, _) = Parse(before);

        // Act & Assert
        MarkdownEditing.ContinueList(text, caret).ShouldBeNull();
    }

    private static void Check(MarkdownFormat format, string before, string after)
    {
        // Arrange
        var (text, start, end) = Parse(before);

        // Act
        var edit = MarkdownEditing.Apply(text, start, end, format);

        // Assert
        Render(edit.ApplyTo(text), edit.SelectionStart, edit.SelectionEnd).ShouldBe(after);
    }

    private static (string Text, int Start, int End) Parse(string marked)
    {
        var caret = marked.IndexOf('|', StringComparison.Ordinal);
        if (caret >= 0)
        {
            return (marked.Remove(caret, 1), caret, caret);
        }

        var start = marked.IndexOf('⟨', StringComparison.Ordinal);
        var end = marked.IndexOf('⟩', StringComparison.Ordinal) - 1;
        return (marked.Replace("⟨", string.Empty, StringComparison.Ordinal).Replace("⟩", string.Empty, StringComparison.Ordinal), start, end);
    }

    private static string Render(string text, int start, int end)
        => start == end ? text.Insert(start, "|") : text.Insert(end, "⟩").Insert(start, "⟨");
}

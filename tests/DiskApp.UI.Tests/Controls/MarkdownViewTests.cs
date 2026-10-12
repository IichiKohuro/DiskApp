using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.VisualTree;

using DiskApp.UI.Controls;

namespace DiskApp.UI.Tests.Controls;

/// <summary>
/// Просмотр Markdown заметок (PLAN.md, этап 23): заголовки, списки с флажками, пути к файлам — ссылки, флажок щелчком
/// меняет нужное место текста, открываются только веб-адреса и почта.
/// </summary>
public sealed class MarkdownViewTests
{
    [Fact]
    public void ToggleTask_SwapsMarkAtIndex_OtherTextUntouched()
    {
        // Arrange
        const string text = "- [ ] первое\n- [x] второе";

        // Act
        var checkedFirst = MarkdownView.ToggleTask(text, new MarkdownTaskToggle(2, true));
        var uncheckedSecond = MarkdownView.ToggleTask(text, new MarkdownTaskToggle(text.IndexOf("[x]", StringComparison.Ordinal), false));
        var wrongIndex = MarkdownView.ToggleTask(text, new MarkdownTaskToggle(4, true));
        var outside = MarkdownView.ToggleTask(text, new MarkdownTaskToggle(text.Length - 1, true));

        // Assert
        checkedFirst.ShouldBe("- [x] первое\n- [x] второе");
        uncheckedSecond.ShouldBe("- [ ] первое\n- [ ] второе");
        wrongIndex.ShouldBe(text);
        outside.ShouldBe(text);
    }

    [Theory]
    [InlineData("https://example.com/a", "https://example.com/a")]
    [InlineData("www.example.com", "https://www.example.com/")]
    [InlineData("mailto:me@example.com", "mailto:me@example.com")]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("file:///C:/Windows/notepad.exe", null)]
    public void WebAddress_OnlyWebAndMail(string target, string? expected)
    {
        // Act
        var uri = MarkdownView.WebAddress(target);

        // Assert
        uri?.AbsoluteUri.ShouldBe(expected);
        (uri is null).ShouldBe(expected is null);
    }

    [Fact]
    public Task Build_HeadingsParagraphsLists_BecomeTextBlocksAndCheckBoxes() => HeadlessTest.RunAsync(static () =>
    {
        // Arrange
        var view = new MarkdownView { FontSize = 14 };

        // Act
        var panel = (StackPanel)view.Build("# Проект\n\nОбычный **жирный** текст\n\n- [ ] дело\n- [x] сделано\n\n1. раз\n2. два\n\n---\n\n> цитата");

        // Assert
        var heading = panel.Children[0].ShouldBeOfType<TextBlock>();
        heading.FontWeight.ShouldBe(FontWeight.SemiBold);
        heading.FontSize.ShouldBeGreaterThan(14);
        var paragraph = panel.Children[1].ShouldBeOfType<TextBlock>();
        paragraph.Inlines!.OfType<Span>().ShouldHaveSingleItem().FontWeight.ShouldBe(FontWeight.SemiBold);
        var boxes = Descendants<CheckBox>(panel.Children[2]);
        boxes.Select(static box => box.IsChecked).ShouldBe([false, true]);
        Descendants<TextBlock>(panel.Children[3]).Select(static block => block.Text).ShouldContain("2.");
        panel.Children[4].ShouldBeOfType<Border>().Height.ShouldBe(1);
        panel.Children[5].ShouldBeOfType<Border>().BorderThickness.Left.ShouldBe(3);
    });

    [Fact]
    public Task TaskBox_Clicked_ReportsSourceIndexOfItsMark() => HeadlessTest.RunAsync(static () =>
    {
        // Arrange
        const string text = "# Дела\n\n- [ ] первое\n- [ ] второе";
        var view = new MarkdownView();
        var toggles = new List<MarkdownTaskToggle>();
        view.TaskToggled += (_, toggle) => toggles.Add(toggle);
        var boxes = Descendants<CheckBox>(view.Build(text));

        // Act
        boxes[1].IsChecked = true;

        // Assert
        var toggle = toggles.ShouldHaveSingleItem();
        toggle.ShouldBe(new MarkdownTaskToggle(text.LastIndexOf("[ ]", StringComparison.Ordinal), true));
        MarkdownView.ToggleTask(text, toggle).ShouldBe("# Дела\n\n- [ ] первое\n- [x] второе");
    });

    [Fact]
    public Task Build_MarkOnNextLineOfSameItem_StaysText() => HeadlessTest.RunAsync(static () =>
    {
        // Arrange: строка без «- » продолжает пункт выше — её «[x]» не флажок, а текст
        var view = new MarkdownView();

        // Act
        var panel = view.Build("- [ ] Сдать\n[x] Смета");

        // Assert
        Descendants<CheckBox>(panel).ShouldHaveSingleItem().IsChecked.ShouldBe(false);
        var text = Descendants<TextBlock>(panel).SelectMany(static block => block.Inlines ?? []).OfType<Run>().Select(static run => run.Text);
        string.Concat(text).ShouldContain("[x] Смета");
    });

    [Fact]
    public Task Build_WindowsPathInText_BecomesLinkWithoutTrailingDot() => HeadlessTest.RunAsync(static () =>
    {
        // Arrange
        var view = new MarkdownView();

        // Act
        var paragraph = ((StackPanel)view.Build(@"Папка проекта: D:\Work\417. Сайт: https://example.com")).Children[0].ShouldBeOfType<TextBlock>();
        var links = paragraph.Inlines!.OfType<MarkdownLink>().ToList();

        // Assert: ссылки — текст абзаца, переносятся вместе с ним
        links.Select(static link => (link.Target, link.Text)).ShouldBe([(@"D:\Work\417", @"D:\Work\417"), ("https://example.com", "https://example.com")]);
        paragraph.Inlines!.OfType<Run>().Select(static run => run.Text).ShouldContain(". Сайт: ");
    });

    [Fact]
    public Task LinkAt_PointOverLinkText_FindsThatLink() => HeadlessTest.RunAsync(static () =>
    {
        // Arrange: ссылка после переноса строки — позиция считается с учётом «\n»
        var view = new MarkdownView();
        var paragraph = ((StackPanel)view.Build("Первая строка\nвторая [сайт](https://example.com) и путь C:\\Temp\\a.txt")).Children[0].ShouldBeOfType<TextBlock>();
        paragraph.Measure(new Avalonia.Size(800, 400));
        paragraph.Arrange(new Avalonia.Rect(0, 0, 800, 400));
        var text = string.Concat(paragraph.Inlines!.OfType<Run>().Select(static run => run.Text));

        // Act
        Avalonia.Point Center(int index)
            => paragraph.TextLayout.HitTestTextPosition(index).Center;
        var site = MarkdownView.LinkAt(paragraph, Center(text.IndexOf("сайт", StringComparison.Ordinal) + 1));
        var path = MarkdownView.LinkAt(paragraph, Center(text.IndexOf(@"C:\Temp", StringComparison.Ordinal) + 3));
        var plain = MarkdownView.LinkAt(paragraph, Center(text.IndexOf("вторая", StringComparison.Ordinal) + 1));

        // Assert
        text.ShouldStartWith("Первая строка\nвторая ");
        site.ShouldNotBeNull().Target.ShouldBe("https://example.com");
        path.ShouldNotBeNull().Target.ShouldBe(@"C:\Temp\a.txt");
        plain.ShouldBeNull();
    });

    private static List<T> Descendants<T>(Control root)
        where T : Control
    {
        var result = new List<T>();
        Collect(root, result);
        return result;
    }

    private static void Collect<T>(Control control, List<T> result)
        where T : Control
    {
        if (control is T match)
        {
            result.Add(match);
        }

        var children = control switch
        {
            Panel panel => panel.Children.ToList(),
            Decorator { Child: { } child } => [child],
            ContentControl { Content: Control content } => [content],
            _ => control.GetVisualChildren().OfType<Control>().ToList(),
        };
        foreach (var child in children)
        {
            Collect(child, result);
        }
    }
}

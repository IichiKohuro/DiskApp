using System.Text;
using System.Text.RegularExpressions;

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;

using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace DiskApp.UI.Controls;

/// <summary>Флажок в просмотре заметки отмечен или снят (этап 23).</summary>
/// <param name="SourceIndex">Позиция <c>[</c> флажка в тексте Markdown.</param>
/// <param name="IsChecked">Новое состояние.</param>
public sealed record MarkdownTaskToggle(int SourceIndex, bool IsChecked);

/// <summary>
/// Ссылка или путь к файлу в просмотре заметки (этап 23): обычный текст абзаца — переносится вместе с ним и стоит ровно
/// по строке; щелчок по нему ловит абзац.
/// </summary>
public sealed class MarkdownLink : Run
{
    /// <summary>Создаёт ссылку.</summary>
    /// <param name="target">Куда ведёт: адрес или путь.</param>
    /// <param name="text">Подпись.</param>
    public MarkdownLink(string target, string text)
        : base(text)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        TextDecorations = Avalonia.Media.TextDecorations.Underline;
    }

    /// <summary>Куда ведёт: адрес или путь к файлу или папке.</summary>
    public string Target { get; }
}

/// <summary>
/// Просмотр Markdown (PLAN.md, этап 23, ADR-0014): Markdig разбирает текст, а подмножество разметки — заголовки, абзацы,
/// жирный и курсив, код, ссылки и пути к файлам, списки, флажки, цитаты, разделители — рисуется своими элементами в стиле
/// приложения. Остальное показывается как текст. Флажки отмечаются щелчком — об этом сообщает <see cref="TaskToggled"/>.
/// </summary>
public sealed partial class MarkdownView : ContentControl
{
    /// <summary>Текст Markdown.</summary>
    public static readonly StyledProperty<string?> MarkdownProperty = AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseTaskLists()
        .UseAutoLinks()
        .UseEmphasisExtras()
        .UsePreciseSourceLocation()
        .Build();

    private static readonly FontFamily Monospace = new("Cascadia Mono, Consolas, Courier New, monospace");

    // Курсор создаётся при первом наведении: в тестах без окна платформы курсоров нет.
    private static readonly Lazy<Cursor> HandCursor = new(static () => new Cursor(StandardCursorType.Hand));

    static MarkdownView()
    {
        MarkdownProperty.Changed.AddClassHandler<MarkdownView>(static (view, _) => view.Rebuild());
        FontSizeProperty.Changed.AddClassHandler<MarkdownView>(static (view, _) => view.Rebuild());
    }

    /// <summary>Флажок отмечен или снят щелчком.</summary>
    public event EventHandler<MarkdownTaskToggle>? TaskToggled;

    /// <summary>Текст Markdown.</summary>
    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    /// <summary>
    /// Меняет состояние флажка в тексте: <c>[ ]</c> ↔ <c>[x]</c> по позиции <c>[</c>; текст без флажка там не меняется.
    /// </summary>
    public static string ToggleTask(string markdown, MarkdownTaskToggle toggle)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(toggle);

        var index = toggle.SourceIndex;
        if (index < 0 || index + 2 >= markdown.Length || markdown[index] != '[' || markdown[index + 2] != ']')
        {
            return markdown;
        }

        return string.Concat(markdown.AsSpan(0, index + 1), toggle.IsChecked ? "x" : " ", markdown.AsSpan(index + 2));
    }

    /// <summary>Адрес ссылки для браузера или почты: «www.site.ru» становится https; другие схемы не открываются (видно тестам).</summary>
    public static Uri? WebAddress(string target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) && !Uri.TryCreate("https://" + target, UriKind.Absolute, out uri))
        {
            return null;
        }

        return uri.Scheme is "http" or "https" or "mailto" ? uri : null;
    }

    /// <summary>
    /// Ссылка под точкой абзаца: позиция символа из раскладки текста — среди ссылок по порядку их текста (видно тестам).
    /// </summary>
    public static MarkdownLink? LinkAt(TextBlock text, Point point)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Inlines is null)
        {
            return null;
        }

        // Все элементы абзаца — Run и Span (переносы строк — «\n» в тексте), так что место ссылки в раскладке считается
        // по длинам текста; попадание — точка внутри прямоугольников её текста (у перенесённой ссылки их несколько).
        var local = point - new Point(text.Padding.Left, text.Padding.Top);
        var offset = 0;
        foreach (var run in Runs(text.Inlines))
        {
            var length = run.Text?.Length ?? 0;
            if (run is MarkdownLink link && length > 0 && text.TextLayout.HitTestTextRange(offset, length).Any(rect => rect.Contains(local)))
            {
                return link;
            }

            offset += length;
        }

        return null;
    }

    /// <summary>Строит элементы просмотра для текста (видно тестам).</summary>
    public Control Build(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var document = Markdig.Markdown.Parse(markdown, Pipeline);
        var panel = new StackPanel { Spacing = 8 };
        foreach (var block in document)
        {
            panel.Children.Add(BuildBlock(block));
        }

        return panel;
    }

    [GeneratedRegex(@"(?<![\w\\/])[A-Za-z]:\\[^\s<>""|?*]+")]
    private static partial Regex WindowsPath();

    private void Rebuild() => Content = Build(Markdown ?? string.Empty);

    private Control BuildBlock(Block block) => block switch
    {
        HeadingBlock heading => Paragraph(heading.Inline, FontSize * heading.Level switch { 1 => 1.6, 2 => 1.35, 3 => 1.15, _ => 1.0 }, FontWeight.SemiBold),
        ParagraphBlock paragraph => Paragraph(paragraph.Inline, FontSize, FontWeight.Normal),
        ListBlock list => List(list),
        QuoteBlock quote => Quote(quote),
        CodeBlock code => Code(code),
        ThematicBreakBlock => Divider(),
        ContainerBlock container => Stack(container),
        LeafBlock leaf => new TextBlock { Text = leaf.Lines.ToString(), TextWrapping = TextWrapping.Wrap },
        _ => new TextBlock(),
    };

    private StackPanel Stack(ContainerBlock container)
    {
        var panel = new StackPanel { Spacing = 6 };
        foreach (var child in container)
        {
            panel.Children.Add(BuildBlock(child));
        }

        return panel;
    }

    private TextBlock Paragraph(ContainerInline? inline, double size, FontWeight weight)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = size, FontWeight = weight };
        if (inline is not null)
        {
            AddInlines(text.Inlines!, inline);
        }

        if (Links(text.Inlines!).Any())
        {
            text.PointerMoved += (_, e) => Hover(text, LinkAt(text, e.GetPosition(text)));
            text.PointerExited += (_, _) => Hover(text, null);
            text.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton == MouseButton.Left && LinkAt(text, e.GetPosition(text)) is { } link)
                {
                    e.Handled = true;
                    _ = OpenAsync(link.Target);
                }
            };
        }

        return text;
    }

    private static IEnumerable<Run> Runs(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is Run run)
            {
                yield return run;
            }
            else if (inline is Span span)
            {
                foreach (var nested in Runs(span.Inlines))
                {
                    yield return nested;
                }
            }
        }
    }

    private static IEnumerable<MarkdownLink> Links(InlineCollection inlines) => Runs(inlines).OfType<MarkdownLink>();

    private static void Hover(TextBlock text, MarkdownLink? link)
    {
        text.Cursor = link is null ? null : HandCursor.Value;
        if (!Equals(ToolTip.GetTip(text), link?.Target))
        {
            ToolTip.SetTip(text, link?.Target);
        }
    }

    private StackPanel List(ListBlock list)
    {
        var panel = new StackPanel { Spacing = 4 };
        var number = int.TryParse(list.OrderedStart, out var start) ? start : 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            var task = (item.FirstOrDefault() as ParagraphBlock)?.Inline?.FirstChild is TaskList first && IsItemTask(first) ? first : null;
            Control marker = task is not null
                ? TaskBox(task)
                : new TextBlock { Text = list.IsOrdered ? $"{number}." : "•", Margin = new Thickness(4, 0, 8, 0), MinWidth = 16 };
            number++;
            var content = Stack(item);
            Grid.SetColumn(content, 1);
            row.Children.Add(marker);
            row.Children.Add(content);
            panel.Children.Add(row);
        }

        return panel;
    }

    private CheckBox TaskBox(TaskList task)
    {
        var box = new CheckBox { IsChecked = task.Checked, MinWidth = 0, Padding = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(box, task.Checked ? "[x]" : "[ ]");
        var index = task.Span.Start;
        box.IsCheckedChanged += (_, _) => TaskToggled?.Invoke(this, new MarkdownTaskToggle(index, box.IsChecked == true));
        return box;
    }

    private Border Quote(QuoteBlock quote)
    {
        var border = new Border { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 2, 0, 2), Child = Stack(quote) };
        border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("AccentFillColorDefaultBrush"));
        return border;
    }

    private Border Code(CodeBlock code)
    {
        var lines = code.Lines.ToString();
        var border = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8),
            Child = new SelectableTextBlock { Text = lines, FontFamily = Monospace, FontSize = FontSize * 0.95, TextWrapping = TextWrapping.Wrap },
        };
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable("SubtleFillColorSecondaryBrush"));
        return border;
    }

    private static Border Divider()
    {
        var line = new Border { Height = 1, Margin = new Thickness(0, 4) };
        line.Bind(Border.BackgroundProperty, line.GetResourceObservable("DividerStrokeColorDefaultBrush"));
        return line;
    }

    private void AddInlines(InlineCollection target, ContainerInline container)
    {
        // Markdig режет текст на куски у «\» и других особых знаков: путь D:\Work\417 ищется в склеенном тексте.
        var text = new StringBuilder();
        foreach (var inline in container)
        {
            if (inline is LiteralInline literal)
            {
                text.Append(literal.Content.ToString());
                continue;
            }

            if (text.Length > 0)
            {
                AddText(target, text.ToString());
                text.Clear();
            }

            switch (inline)
            {
                case TaskList task when IsItemTask(task):
                    // Флажок уже нарисован в строке списка.
                    break;
                case TaskList task:
                    // «[x]» в середине пункта — просто текст: флажок только в начале пункта.
                    target.Add(new Run(task.Checked ? "[x]" : "[ ]"));
                    break;
                case EmphasisInline emphasis:
                    var span = new Span();
                    if (emphasis.DelimiterChar == '~')
                    {
                        span.TextDecorations = TextDecorations.Strikethrough;
                    }
                    else if (emphasis.DelimiterCount >= 2)
                    {
                        span.FontWeight = FontWeight.SemiBold;
                    }
                    else
                    {
                        span.FontStyle = FontStyle.Italic;
                    }

                    AddInlines(span.Inlines, emphasis);
                    target.Add(span);
                    break;
                case CodeInline code:
                    var run = new Run(code.Content) { FontFamily = Monospace };
                    run.Bind(TextElement.BackgroundProperty, this.GetResourceObservable("SubtleFillColorSecondaryBrush"));
                    target.Add(run);
                    break;
                case LinkInline { IsImage: false } link:
                    target.Add(Link(link.Url ?? string.Empty, LinkText(link)));
                    break;
                case AutolinkInline auto:
                    target.Add(Link(auto.Url, auto.Url));
                    break;
                case LineBreakInline:
                    // В заметке новая строка — новая строка и в просмотре, как в обычных блокнотах, а не пробел по CommonMark.
                    target.Add(new Run("\n"));
                    break;
                case ContainerInline nested:
                    AddInlines(target, nested);
                    break;
                case LeafInline leaf:
                    target.Add(new Run(leaf.ToString()));
                    break;
            }
        }

        if (text.Length > 0)
        {
            AddText(target, text.ToString());
        }
    }

    // Флажок пункта — первое в первом абзаце пункта списка; Markdig находит «[x]» и в начале следующей строки того же абзаца.
    private static bool IsItemTask(TaskList task)
        => task.Parent is { } inline
            && ReferenceEquals(inline.FirstChild, task)
            && inline.ParentBlock is ParagraphBlock paragraph
            && paragraph.Parent is ListItemBlock item
            && ReferenceEquals(item.FirstOrDefault(), paragraph);

    private static string LinkText(LinkInline link)
    {
        var text = string.Concat(link.Descendants<LiteralInline>().Select(static literal => literal.Content.ToString()));
        return text.Length > 0 ? text : link.Url ?? string.Empty;
    }

    // Пути к файлам и папкам Windows в тексте — тоже ссылки: открываются, как двойным щелчком в Проводнике.
    private void AddText(InlineCollection target, string text)
    {
        var position = 0;
        foreach (Match match in WindowsPath().Matches(text))
        {
            // «Папка D:\Work\417.» — точка и запятая в конце принадлежат предложению, а не пути.
            var path = match.Value.TrimEnd('.', ',', ';', ':', ')', '»', '"', '\'');
            if (path.Length < 4)
            {
                continue;
            }

            if (match.Index > position)
            {
                target.Add(new Run(text[position..match.Index]));
            }

            target.Add(Link(path, path));
            position = match.Index + path.Length;
        }

        if (position < text.Length)
        {
            target.Add(new Run(text[position..]));
        }
    }

    private MarkdownLink Link(string target, string text)
    {
        var link = new MarkdownLink(target, text);
        link.Bind(TextElement.ForegroundProperty, this.GetResourceObservable("AccentTextFillColorPrimaryBrush"));
        return link;
    }

    private async Task OpenAsync(string target)
    {
        if (TopLevel.GetTopLevel(this)?.Launcher is not { } launcher)
        {
            return;
        }

        if (WindowsPath().IsMatch(target))
        {
            // Пути, которого уже нет, просто не открываются: адресом в браузере он не становится.
            if (Directory.Exists(target))
            {
                await launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(target));
            }
            else if (File.Exists(target))
            {
                await launcher.LaunchFileInfoAsync(new FileInfo(target));
            }
        }
        else if (WebAddress(target) is { } uri)
        {
            await launcher.LaunchUriAsync(uri);
        }
    }
}

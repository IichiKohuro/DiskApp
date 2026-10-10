using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using Avalonia.Media;

namespace DiskApp.UI.Controls;

/// <summary>
/// Текст с подсветкой совпадений: все вхождения каждого слова из <see cref="Highlight"/> выделяются
/// начертанием <see cref="HighlightFontWeight"/> и цветом <see cref="HighlightBrush"/> (по умолчанию — акцент темы).
/// Сравнение без учёта регистра, кириллица поддерживается.
/// </summary>
public sealed class HighlightTextBlock : UserControl
{
    /// <summary>Свойство <see cref="Text"/>.</summary>
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<HighlightTextBlock, string?>(nameof(Text));

    /// <summary>Свойство <see cref="Highlight"/>.</summary>
    public static readonly StyledProperty<string?> HighlightProperty =
        AvaloniaProperty.Register<HighlightTextBlock, string?>(nameof(Highlight));

    /// <summary>Свойство <see cref="HighlightBrush"/>.</summary>
    public static readonly StyledProperty<IBrush?> HighlightBrushProperty =
        AvaloniaProperty.Register<HighlightTextBlock, IBrush?>(nameof(HighlightBrush));

    /// <summary>Свойство <see cref="HighlightFontWeight"/>.</summary>
    public static readonly StyledProperty<FontWeight> HighlightFontWeightProperty =
        AvaloniaProperty.Register<HighlightTextBlock, FontWeight>(nameof(HighlightFontWeight), FontWeight.SemiBold);

    /// <summary>Свойство <see cref="TextTrimming"/>.</summary>
    public static readonly StyledProperty<TextTrimming> TextTrimmingProperty =
        AvaloniaProperty.Register<HighlightTextBlock, TextTrimming>(nameof(TextTrimming), TextTrimming.CharacterEllipsis);

    private const string AccentBrushKey = "AccentTextFillColorPrimaryBrush";

    private readonly TextBlock _textBlock = new();

    /// <summary>
    /// Создаёт контрол; цвет подсветки по умолчанию берётся из ресурса темы и следует за сменой темы.
    /// </summary>
    public HighlightTextBlock()
    {
        Content = _textBlock;
        _textBlock.Bind(TextBlock.TextTrimmingProperty, this.GetObservable(TextTrimmingProperty));
        this.Bind(HighlightBrushProperty, this.GetResourceObservable(AccentBrushKey), BindingPriority.Style);
    }

    /// <summary>Текст целиком.</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Слова для подсветки через пробел; пусто — без подсветки.</summary>
    public string? Highlight
    {
        get => GetValue(HighlightProperty);
        set => SetValue(HighlightProperty, value);
    }

    /// <summary>Цвет подсвеченных фрагментов.</summary>
    public IBrush? HighlightBrush
    {
        get => GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    /// <summary>Начертание подсвеченных фрагментов.</summary>
    public FontWeight HighlightFontWeight
    {
        get => GetValue(HighlightFontWeightProperty);
        set => SetValue(HighlightFontWeightProperty, value);
    }

    /// <summary>Обрезка текста, если он не помещается.</summary>
    public TextTrimming TextTrimming
    {
        get => GetValue(TextTrimmingProperty);
        set => SetValue(TextTrimmingProperty, value);
    }

    /// <summary>Фрагменты текста после разбиения на обычные и подсвеченные; для тестов.</summary>
    public IReadOnlyList<Run> Runs => _textBlock.Inlines?.OfType<Run>().ToList() ?? [];

    /// <summary>
    /// Разбивает текст на фрагменты: диапазоны, покрытые словами подсветки, и всё остальное между ними.
    /// Пересекающиеся вхождения объединяются.
    /// </summary>
    /// <returns>Пары «текст, подсвечен ли».</returns>
    public static IReadOnlyList<(string Text, bool IsMatch)> Split(string text, string? highlight)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            return [];
        }

        var tokens = (highlight ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return [(text, false)];
        }

        var marks = new bool[text.Length];
        foreach (var token in tokens)
        {
            var start = 0;
            int index;
            while (start < text.Length && (index = text.IndexOf(token, start, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                Array.Fill(marks, true, index, token.Length);
                start = index + token.Length;
            }
        }

        var parts = new List<(string, bool)>();
        var position = 0;
        while (position < text.Length)
        {
            var isMatch = marks[position];
            var end = position;
            while (end < text.Length && marks[end] == isMatch)
            {
                end++;
            }

            parts.Add((text[position..end], isMatch));
            position = end;
        }

        return parts;
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TextProperty
            || change.Property == HighlightProperty
            || change.Property == HighlightBrushProperty
            || change.Property == HighlightFontWeightProperty)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        var inlines = _textBlock.Inlines;
        if (inlines is null)
        {
            inlines = [];
            _textBlock.Inlines = inlines;
        }

        inlines.Clear();

        foreach (var (part, isMatch) in Split(Text ?? string.Empty, Highlight))
        {
            var run = new Run(part);
            if (isMatch)
            {
                run.FontWeight = HighlightFontWeight;
                if (HighlightBrush is { } brush)
                {
                    run.Foreground = brush;
                }
            }

            inlines.Add(run);
        }
    }
}

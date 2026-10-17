using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

using DiskApp.UI.Models;

using FluentAvalonia.Styling;

namespace DiskApp.UI.Controls;

/// <summary>Как номер ложится на иконку: строки и размер шрифта.</summary>
/// <param name="Lines">Одна или две строки; если номер не поместился и в две, вторая заканчивается «…».</param>
/// <param name="FontSize">Размер шрифта.</param>
public sealed record ProjectBadgeLayout(IReadOnlyList<string> Lines, double FontSize);

/// <summary>
/// Иконка проекта (PLAN.md, этап 24.2, ADR-0017): квадрат со скруглёнными углами, цветной фон и номер. Номер любой
/// длины помещается — сначала уменьшается шрифт, затем номер переносится на вторую строку (по «-», пробелу или
/// посередине); цвет текста — белый или почти чёрный, что контрастнее к фону. При высокой контрастности — системные
/// цвета окна с рамкой, как у остальных элементов.
/// </summary>
public sealed class ProjectBadge : Control
{
    /// <summary>Свойство <see cref="Number"/>.</summary>
    public static readonly StyledProperty<string?> NumberProperty = AvaloniaProperty.Register<ProjectBadge, string?>(nameof(Number));

    /// <summary>Свойство <see cref="Color"/>.</summary>
    public static readonly StyledProperty<string?> ColorProperty = AvaloniaProperty.Register<ProjectBadge, string?>(nameof(Color));

    /// <summary>Свойство <see cref="BadgeSize"/>.</summary>
    public static readonly StyledProperty<double> BadgeSizeProperty = AvaloniaProperty.Register<ProjectBadge, double>(nameof(BadgeSize), 32);

    /// <summary>Шрифт; наследуется, как у текста.</summary>
    public static readonly StyledProperty<FontFamily> FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner<ProjectBadge>();

    private static readonly Color DarkText = Avalonia.Media.Color.FromRgb(0x1B, 0x1B, 0x1B);

    static ProjectBadge()
    {
        AffectsMeasure<ProjectBadge>(BadgeSizeProperty);
        AffectsRender<ProjectBadge>(NumberProperty, ColorProperty, BadgeSizeProperty, FontFamilyProperty);
    }

    /// <summary>Создаёт иконку; при смене темы (в том числе высокой контрастности) она перерисовывается.</summary>
    public ProjectBadge() => ActualThemeVariantChanged += (_, _) => InvalidateVisual();

    /// <summary>Номер проекта.</summary>
    public string? Number
    {
        get => GetValue(NumberProperty);
        set => SetValue(NumberProperty, value);
    }

    /// <summary>Цвет фона <c>#RRGGBB</c>; <c>null</c> — цвет палитры по номеру.</summary>
    public string? Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    /// <summary>Сторона квадрата.</summary>
    public double BadgeSize
    {
        get => GetValue(BadgeSizeProperty);
        set => SetValue(BadgeSizeProperty, value);
    }

    /// <summary>Шрифт номера.</summary>
    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    /// <summary>Фон: свой цвет или цвет палитры по номеру — у одного номера всегда один цвет.</summary>
    public static Color BackgroundFor(string? color, string? number)
    {
        if (color is not null && Avalonia.Media.Color.TryParse(color, out var parsed))
        {
            return parsed;
        }

        var key = (number ?? string.Empty).Trim().ToUpperInvariant();
        var hash = 0;
        foreach (var ch in key)
        {
            hash = unchecked((hash * 31) + ch);
        }

        return Avalonia.Media.Color.Parse(TagPalette.Colors[(int)((uint)hash % TagPalette.Colors.Count)]);
    }

    /// <summary>Цвет номера: белый или почти чёрный — что контрастнее к фону.</summary>
    public static Color ForegroundFor(Color background)
        => ContrastRatio(background, Colors.White) >= ContrastRatio(background, DarkText) ? Colors.White : DarkText;

    /// <summary>Контраст двух цветов по WCAG 2.x: 1–21.</summary>
    public static double ContrastRatio(Color first, Color second)
    {
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>
    /// Раскладка номера в квадрат стороной <paramref name="size"/>: одна строка крупно, если влезает; иначе мельче;
    /// иначе две или три строки; иначе самым мелким шрифтом с «…» в последней строке.
    /// </summary>
    /// <param name="number">Номер.</param>
    /// <param name="size">Сторона квадрата.</param>
    /// <param name="measureWidth">Ширина строки при данном размере шрифта.</param>
    public static ProjectBadgeLayout Fit(string number, double size, Func<string, double, double> measureWidth)
    {
        ArgumentNullException.ThrowIfNull(number);
        ArgumentNullException.ThrowIfNull(measureWidth);

        var text = number.Trim();
        var available = InnerSize(size);
        var maxFont = size * (text.Length <= 2 ? 0.5 : 0.42);
        var minFont = MinFontSize(size);
        if (text.Length <= 1)
        {
            return new ProjectBadgeLayout([text], maxFont);
        }

        for (var count = 1; count <= Math.Min(MaxLines, text.Length); count++)
        {
            var lines = SplitInto(text, count);
            var font = Math.Min(available / (count * LineHeight), lines.Min(line => FontToFit(line, maxFont, available, measureWidth)));
            if (font >= minFont)
            {
                return new ProjectBadgeLayout(lines, font);
            }
        }

        // Не поместилось и в три строки: самый мелкий шрифт, строки — сколько влезет, последняя — с «…».
        var smallest = Math.Min(minFont, available / (MaxLines * LineHeight));
        var result = new List<string>();
        var rest = text;
        while (rest.Length > 0 && result.Count < MaxLines)
        {
            var line = Take(rest, smallest, available, measureWidth, ellipsis: result.Count == MaxLines - 1);
            result.Add(line);
            rest = line.EndsWith('…') ? string.Empty : rest[line.Length..].TrimStart();
        }

        return new ProjectBadgeLayout(result, smallest);
    }

    /// <summary>Поле внутри квадрата, где помещается номер.</summary>
    public static double InnerSize(double size) => size - (2 * Padding(size));

    /// <summary>Высота строки к размеру шрифта.</summary>
    public const double LineHeight = 1.2;

    /// <summary>Больше строк номер не занимает.</summary>
    public const int MaxLines = 3;

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize) => new(BadgeSize, BadgeSize);

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize) => new(Math.Min(BadgeSize, finalSize.Width), Math.Min(BadgeSize, finalSize.Height));

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
        {
            return;
        }

        var rect = new Rect((Bounds.Width - size) / 2, (Bounds.Height - size) / 2, size, size);
        var radius = size * 0.22;
        var highContrast = ActualThemeVariant == FluentAvaloniaTheme.HighContrastTheme;
        IBrush background;
        IBrush foreground;
        IPen? border = null;
        if (highContrast)
        {
            background = new SolidColorBrush(Resource("SystemColorWindowColor", Colors.Black));
            var text = Resource("SystemColorWindowTextColor", Colors.White);
            foreground = new SolidColorBrush(text);
            border = new Pen(new SolidColorBrush(text), Math.Max(1, size / 24));
        }
        else
        {
            var color = BackgroundFor(Color, Number);
            background = new SolidColorBrush(color);
            foreground = new SolidColorBrush(ForegroundFor(color));
        }

        context.DrawRectangle(background, border, rect, radius, radius);

        var typeface = new Typeface(FontFamily, FontStyle.Normal, FontWeight.SemiBold);
        var layout = Fit(Number ?? string.Empty, size, (line, fontSize) => Format(line, typeface, fontSize, foreground).WidthIncludingTrailingWhitespace);
        var lineHeight = layout.FontSize * LineHeight;
        var top = rect.Y + ((size - (lineHeight * layout.Lines.Count)) / 2);
        foreach (var line in layout.Lines)
        {
            var formatted = Format(line, typeface, layout.FontSize, foreground);
            context.DrawText(formatted, new Point(rect.X + ((size - formatted.WidthIncludingTrailingWhitespace) / 2), top + ((lineHeight - formatted.Height) / 2)));
            top += lineHeight;
        }
    }

    private static double Padding(double size) => Math.Max(2, size * 0.1);

    private static double MinFontSize(double size) => Math.Max(6, size * 0.14);

    private static double FontToFit(string line, double maxFont, double available, Func<string, double, double> measureWidth)
    {
        var width = measureWidth(line, maxFont);
        return width <= available ? maxFont : maxFont * available / width * 0.98;
    }

    // Перенос — после «-», «_», «/», «.» или на пробеле ближе к ровному делению; без них — по ровному делению.
    private static string[] SplitInto(string text, int count)
    {
        var parts = new string[count];
        var start = 0;
        for (var k = 1; k < count; k++)
        {
            var target = text.Length * k / (double)count;
            var window = text.Length / (2.0 * count);
            var cut = -1;
            for (var i = start + 1; i < text.Length; i++)
            {
                var breakable = text[i - 1] is '-' or '_' or '/' or '.' || text[i] == ' ';
                if (breakable && Math.Abs(i - target) <= window && (cut < 0 || Math.Abs(i - target) < Math.Abs(cut - target)))
                {
                    cut = i;
                }
            }

            if (cut < 0)
            {
                cut = Math.Clamp((int)Math.Round(target), start + 1, text.Length - (count - k));
            }

            parts[k - 1] = text[start..cut].Trim();
            start = cut;
        }

        parts[count - 1] = text[start..].Trim();
        return parts;
    }

    private static string Take(string text, double fontSize, double available, Func<string, double, double> measureWidth, bool ellipsis)
    {
        if (measureWidth(text, fontSize) <= available)
        {
            return text;
        }

        if (!ellipsis)
        {
            var length = text.Length - 1;
            while (length > 1 && measureWidth(text[..length], fontSize) > available)
            {
                length--;
            }

            return text[..length];
        }

        var cut = text.Length - 1;
        while (cut > 0 && measureWidth(text[..cut] + "…", fontSize) > available)
        {
            cut--;
        }

        return text[..cut] + "…";
    }

    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }

    private static FormattedText Format(string text, Typeface typeface, double fontSize, IBrush foreground)
        => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, fontSize, foreground);

    private Color Resource(string key, Color fallback)
        => this.TryFindResource(key, ActualThemeVariant, out var value) && value is Color color ? color : fallback;
}

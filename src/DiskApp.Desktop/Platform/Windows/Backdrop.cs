using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace DiskApp.Desktop.Platform.Windows;

/// <summary>
/// Фон главного окна (Windows 11): Mica, Acrylic или сплошной цвет темы (настройка «Фон окон», PLAN.md, этап 9.1).
/// Avalonia включает материал через <c>TransparencyLevelHint</c>, а цвет поверх него задаёт полупрозрачный фон окна в цвете
/// темы, как в примере FluentAvalonia. Если материал недоступен (Windows 10, отключённая прозрачность), окно получает
/// сплошной цвет темы.
/// </summary>
internal static class Backdrop
{
    private const string BaseColorKey = "SolidBackgroundFillColorBase";
    private const string BaseBrushKey = "SolidBackgroundFillColorBaseBrush";

    private static readonly HashSet<Window> Tracked = [];

    /// <summary>Уровни прозрачности по настройке: желаемый и запасные.</summary>
    public static IReadOnlyList<WindowTransparencyLevel> Levels(Core.Settings.BackdropMode mode) => mode switch
    {
        Core.Settings.BackdropMode.Mica => [WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None],
        Core.Settings.BackdropMode.Acrylic => [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None],
        _ => [WindowTransparencyLevel.None],
    };

    /// <summary>
    /// Включает Mica (по умолчанию) и держит цвет фона в соответствии с темой и фактическим уровнем прозрачности.
    /// </summary>
    public static void ApplyMica(Window window) => Apply(window, Core.Settings.BackdropMode.Mica);

    /// <summary>
    /// Включает выбранный фон; повторный вызов только меняет материал.
    /// </summary>
    public static void Apply(Window window, Core.Settings.BackdropMode mode)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        window.TransparencyLevelHint = Levels(mode);
        UpdateBackground(window);

        if (!Tracked.Add(window))
        {
            return;
        }

        window.ActualThemeVariantChanged += (_, _) => UpdateBackground(window);
        window.PropertyChanged += (_, args) =>
        {
            if (args.Property == TopLevel.ActualTransparencyLevelProperty)
            {
                UpdateBackground(window);
            }
        };
        window.Closed += (_, _) => Tracked.Remove(window);
    }

    private static void UpdateBackground(Window window)
    {
        var theme = window.ActualThemeVariant;
        var level = window.ActualTransparencyLevel;

        if (!level.Equals(WindowTransparencyLevel.Mica) && !level.Equals(WindowTransparencyLevel.AcrylicBlur))
        {
            window.Background = window.TryFindResource(BaseBrushKey, theme, out var brush) && brush is IBrush solid ? solid : null;
            return;
        }

        var isDark = theme == ThemeVariant.Dark;
        var baseColor = window.TryFindResource(BaseColorKey, theme, out var value) && value is Color color
            ? color
            : isDark ? Color.FromRgb(32, 32, 32) : Color.FromRgb(243, 243, 243);

        // Mica сама светлее или темнее базового цвета, поэтому поверх неё нужен более контрастный полупрозрачный слой.
        var tinted = isDark ? Mix(baseColor, Colors.Black, 0.8) : Mix(baseColor, Colors.White, 0.5);
        // Acrylic размывает то, что под окном, — слой прозрачнее, чтобы размытие было видно.
        var acrylic = level.Equals(WindowTransparencyLevel.AcrylicBlur);
        window.Background = new ImmutableSolidColorBrush(tinted, acrylic ? (isDark ? 0.62 : 0.72) : (isDark ? 0.78 : 0.9));
    }

    private static Color Mix(Color from, Color to, double amount)
    {
        static byte Channel(byte a, byte b, double t) => (byte)Math.Clamp(Math.Round(a + ((b - a) * t)), 0, 255);
        return Color.FromRgb(Channel(from.R, to.R, amount), Channel(from.G, to.G, amount), Channel(from.B, to.B, amount));
    }
}

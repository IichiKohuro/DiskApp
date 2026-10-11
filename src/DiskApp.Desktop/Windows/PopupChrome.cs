using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

using DiskApp.Core.Settings;

namespace DiskApp.Desktop.Windows;

/// <summary>
/// Общее у всплывающих окон — быстрого поиска и Drag&amp;Drop: фон Acrylic со сплошной подложкой, когда размытие
/// недоступно, и появление сдвигом с проявлением за 150 мс (отключается при «уменьшить движение»).
/// </summary>
internal static class PopupChrome
{
    private static readonly TimeSpan ShowDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TransformOperations Resting = TransformOperations.Parse("translateY(0px)");

    /// <summary>Какие уровни прозрачности просить у окна: сплошной фон или Acrylic с запасными.</summary>
    public static IReadOnlyList<WindowTransparencyLevel> TransparencyFor(BackdropMode backdrop)
        => backdrop == BackdropMode.Solid
            ? [WindowTransparencyLevel.None]
            : [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.None];

    /// <summary>
    /// Показывает Acrylic или сплошную подложку по фактическому уровню прозрачности и перекрашивает запасной фон окна в
    /// цвет текущей темы: сам он за сменой темы не следит.
    /// </summary>
    public static void UpdateBackdrop(Window window, Control? acrylic, Control? solid)
    {
        ArgumentNullException.ThrowIfNull(window);

        // Уровень прозрачности меняется ещё при загрузке разметки, до присвоения именованных полей.
        if (acrylic is null || solid is null)
        {
            return;
        }

        var level = window.ActualTransparencyLevel;
        acrylic.IsVisible = level.Equals(WindowTransparencyLevel.AcrylicBlur) || level.Equals(WindowTransparencyLevel.Blur);
        solid.IsVisible = !acrylic.IsVisible;

        if (window.TryFindResource("SolidBackgroundFillColorBaseBrush", window.ActualThemeVariant, out var brush) && brush is IBrush fill)
        {
            window.TransparencyBackgroundFallback = fill;
        }
    }

    /// <summary>Готовит содержимое к появлению: прозрачное и сдвинутое на <paramref name="offset"/> по вертикали.</summary>
    public static void PrepareShow(Control root, bool reduceMotion, double offset)
    {
        ArgumentNullException.ThrowIfNull(root);

        root.Transitions = null;
        if (reduceMotion)
        {
            root.Opacity = 1;
            root.RenderTransform = null;
            return;
        }

        root.Opacity = 0;
        root.RenderTransform = TransformOperations.Parse(FormattableString.Invariant($"translateY({offset}px)"));
    }

    /// <summary>Проигрывает появление после <see cref="PrepareShow"/>.</summary>
    public static void PlayShow(Control root, bool reduceMotion)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (reduceMotion)
        {
            return;
        }

        root.Transitions =
        [
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = ShowDuration, Easing = new CubicEaseOut() },
            new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = ShowDuration, Easing = new CubicEaseOut() },
        ];
        Dispatcher.UIThread.Post(() =>
        {
            root.Opacity = 1;
            root.RenderTransform = Resting;
        }, DispatcherPriority.Render);
    }
}

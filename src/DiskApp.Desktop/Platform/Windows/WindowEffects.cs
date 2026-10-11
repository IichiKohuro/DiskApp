using System.Runtime.Versioning;

using Avalonia;
using Avalonia.Controls;

namespace DiskApp.Desktop.Platform.Windows;

/// <summary>
/// Платформенные мелочи для окон: скруглённые углы безрамочных окон через DWM и положение курсора.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowEffects
{
    /// <summary>
    /// Просит DWM скруглить углы окна (Windows 11; на Windows 10 атрибут игнорируется).
    /// </summary>
    /// <returns><c>true</c>, если DWM принял атрибут.</returns>
    public static bool TryApplyRoundedCorners(TopLevel topLevel)
    {
        ArgumentNullException.ThrowIfNull(topLevel);

        var handle = topLevel.TryGetPlatformHandle()?.Handle ?? 0;
        if (handle == 0)
        {
            return false;
        }

        var preference = NativeMethods.DwmwcpRound;
        return NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DwmwaWindowCornerPreference, in preference, sizeof(int)) == 0;
    }

    /// <summary>
    /// Положение курсора в пикселях экрана или <c>null</c>, если его не удалось получить.
    /// </summary>
    public static PixelPoint? GetCursorPosition()
        => NativeMethods.GetCursorPos(out var point) ? new PixelPoint(point.X, point.Y) : null;

    /// <summary>Центр активного окна в пикселях экрана или <c>null</c>.</summary>
    public static PixelPoint? GetForegroundWindowCenter()
    {
        var window = NativeMethods.GetForegroundWindow();
        return window != 0 && NativeMethods.GetWindowRect(window, out var rect) && rect.Right > rect.Left
            ? new PixelPoint((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2)
            : null;
    }
}

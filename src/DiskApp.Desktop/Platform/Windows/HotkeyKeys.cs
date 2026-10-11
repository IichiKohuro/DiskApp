using System.Globalization;

namespace DiskApp.Desktop.Platform.Windows;

/// <summary>
/// Код виртуальной клавиши Windows для имени клавиши из <see cref="UI.Services.Hotkey"/>.
/// </summary>
internal static class HotkeyKeys
{
    /// <summary>Код для <c>RegisterHotKey</c>; <c>null</c> — клавиша не поддерживается.</summary>
    public static uint? ToVirtualKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (key.Length == 1 && (char.IsAsciiLetterUpper(key[0]) || char.IsAsciiDigit(key[0])))
        {
            // VK_A…VK_Z и VK_0…VK_9 совпадают с кодами символов ASCII.
            return key[0];
        }

        if (key.Length > 1 && key[0] == 'F' && int.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var function) && function is >= 1 and <= 24)
        {
            return (uint)(0x70 + function - 1);
        }

        return key switch
        {
            "Space" => 0x20,
            "PageUp" => 0x21,
            "PageDown" => 0x22,
            "End" => 0x23,
            "Home" => 0x24,
            "Left" => 0x25,
            "Up" => 0x26,
            "Right" => 0x27,
            "Down" => 0x28,
            "Insert" => 0x2D,
            "Pause" => 0x13,
            _ => null,
        };
    }
}

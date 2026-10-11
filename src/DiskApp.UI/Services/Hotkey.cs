using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using Avalonia.Input;

namespace DiskApp.UI.Services;

/// <summary>
/// Модификаторы сочетания; значения совпадают с флагами <c>RegisterHotKey</c> (<c>MOD_ALT</c> и т. д.).
/// </summary>
[Flags]
public enum HotkeyModifiers
{
    /// <summary>Без модификаторов.</summary>
    None = 0,

    /// <summary>Alt.</summary>
    Alt = 1,

    /// <summary>Ctrl.</summary>
    Control = 2,

    /// <summary>Shift.</summary>
    Shift = 4,

    /// <summary>Win.</summary>
    Windows = 8,
}

/// <summary>
/// Глобальное сочетание клавиш (PLAN.md, этап 6.5): модификаторы и клавиша. В настройках хранится строкой
/// <c>Ctrl+Alt+D</c>, в интерфейсе показывается как «Ctrl + Alt + D». Нужен хотя бы один из Ctrl, Alt и Win —
/// иначе сочетание перехватит обычный ввод.
/// </summary>
/// <param name="Modifiers">Модификаторы.</param>
/// <param name="Key">Клавиша: буква A–Z, цифра 0–9, F1–F24, Space, Insert, Home, End, PageUp, PageDown, стрелки, Pause.</param>
public sealed record Hotkey(HotkeyModifiers Modifiers, string Key)
{
    private static readonly string[] NamedKeys = ["Space", "Insert", "Home", "End", "PageUp", "PageDown", "Left", "Up", "Right", "Down", "Pause"];

    /// <summary>Сочетание быстрого поиска по умолчанию (ADR-0006).</summary>
    public static Hotkey DefaultQuickSearch { get; } = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "D");

    /// <summary>Запасные сочетания быстрого поиска по убыванию предпочтения (ADR-0006).</summary>
    public static IReadOnlyList<Hotkey> QuickSearchFallbacks { get; } =
    [
        DefaultQuickSearch,
        new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "F"),
        new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "Space"),
    ];

    /// <summary>Сочетание Quick Switch по умолчанию (PLAN.md, этап 8.6, ADR-0008).</summary>
    public static Hotkey DefaultQuickSwitch { get; } = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "G");

    /// <summary>Подпись для интерфейса: «Ctrl + Alt + D».</summary>
    public string DisplayText => string.Join(" + ", Parts());

    /// <summary>
    /// Разбирает строку настроек (<c>Ctrl+Alt+D</c>, регистр и пробелы не важны; <c>Control</c>, <c>Win</c> — синонимы).
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out Hotkey? hotkey)
    {
        hotkey = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        string? key = null;
        foreach (var raw in text.Split('+'))
        {
            var part = raw.Trim();
            var modifier = part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => HotkeyModifiers.Control,
                "ALT" => HotkeyModifiers.Alt,
                "SHIFT" => HotkeyModifiers.Shift,
                "WIN" or "WINDOWS" or "META" => HotkeyModifiers.Windows,
                _ => HotkeyModifiers.None,
            };

            if (modifier != HotkeyModifiers.None)
            {
                if ((modifiers & modifier) != 0)
                {
                    return false;
                }

                modifiers |= modifier;
                continue;
            }

            if (key is not null || NormalizeKey(part) is not { } normalized)
            {
                return false;
            }

            key = normalized;
        }

        if (key is null || (modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Windows)) == 0)
        {
            return false;
        }

        hotkey = new Hotkey(modifiers, key);
        return true;
    }

    /// <summary>Строка для настроек: <c>Ctrl+Alt+D</c>.</summary>
    public override string ToString() => string.Join("+", Parts());

    /// <summary>Жест Avalonia для подписи в меню.</summary>
    public KeyGesture ToGesture()
    {
        var modifiers = KeyModifiers.None;
        if ((Modifiers & HotkeyModifiers.Control) != 0)
        {
            modifiers |= KeyModifiers.Control;
        }

        if ((Modifiers & HotkeyModifiers.Alt) != 0)
        {
            modifiers |= KeyModifiers.Alt;
        }

        if ((Modifiers & HotkeyModifiers.Shift) != 0)
        {
            modifiers |= KeyModifiers.Shift;
        }

        if ((Modifiers & HotkeyModifiers.Windows) != 0)
        {
            modifiers |= KeyModifiers.Meta;
        }

        var key = Key.Length == 1 && char.IsAsciiDigit(Key[0]) ? "D" + Key : Key;
        return new KeyGesture(Enum.Parse<Avalonia.Input.Key>(key), modifiers);
    }

    private static string? NormalizeKey(string part)
    {
        if (part.Length == 1 && (char.IsAsciiLetter(part[0]) || char.IsAsciiDigit(part[0])))
        {
            return part.ToUpperInvariant();
        }

        if (part.Length is 2 or 3 && (part[0] is 'F' or 'f') && int.TryParse(part.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var function) && function is >= 1 and <= 24)
        {
            return "F" + function.ToString(CultureInfo.InvariantCulture);
        }

        return NamedKeys.FirstOrDefault(name => string.Equals(name, part, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<string> Parts()
    {
        if ((Modifiers & HotkeyModifiers.Control) != 0)
        {
            yield return "Ctrl";
        }

        if ((Modifiers & HotkeyModifiers.Alt) != 0)
        {
            yield return "Alt";
        }

        if ((Modifiers & HotkeyModifiers.Shift) != 0)
        {
            yield return "Shift";
        }

        if ((Modifiers & HotkeyModifiers.Windows) != 0)
        {
            yield return "Win";
        }

        yield return Key;
    }
}

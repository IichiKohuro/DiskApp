using System.Globalization;

namespace DiskApp.UI.Localization;

/// <summary>
/// Короткий доступ к строкам интерфейса из кода (PLAN.md, этап 9.2).
/// </summary>
public static class L
{
    /// <summary>Строка по ключу на текущем языке.</summary>
    public static string T(string key) => Localizer.Instance[key];

    /// <summary>Строка по ключу с подстановками <c>{0}</c>, <c>{1}</c>… в культуре интерфейса.</summary>
    public static string F(string key, params object?[] args)
        => string.Format(Localizer.Instance.Culture, Localizer.Instance[key], args);

    /// <summary>Культура интерфейса для чисел и дат.</summary>
    public static CultureInfo Culture => Localizer.Instance.Culture;
}

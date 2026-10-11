using System.Globalization;
using System.Resources;

namespace DiskApp.Server.Localization;

/// <summary>
/// Строки сервера (сообщения API) на языке интерфейса (PLAN.md, этап 9.2): русский — нейтральные ресурсы, английский — в
/// <c>Strings.en.resx</c>. Язык — культура интерфейса потока (<see cref="CultureInfo.CurrentUICulture"/>), которую десктоп
/// задаёт для всего процесса.
/// </summary>
public static class L
{
    private static readonly ResourceManager Resources = new("DiskApp.Server.Localization.Strings", typeof(L).Assembly);

    /// <summary>Строка по ключу; неизвестный ключ возвращается как есть.</summary>
    public static string T(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    /// <summary>Строка по ключу с подстановками <c>{0}</c>, <c>{1}</c>…</summary>
    public static string F(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);
}

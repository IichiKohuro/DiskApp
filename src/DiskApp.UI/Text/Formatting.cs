using System.Globalization;

using DiskApp.UI.Localization;

namespace DiskApp.UI.Text;

/// <summary>
/// Подписи для интерфейса: размеры, относительное время, количества со склонением — на языке интерфейса (этап 9.2).
/// </summary>
public static class Formatting
{
    private static readonly string[] SizeUnits = ["Size.B", "Size.KB", "Size.MB", "Size.GB", "Size.TB"];

    /// <summary>
    /// Размер в байтах в удобных единицах: «512 Б», «1,2 МБ», «3 ГБ». Дробная часть только для значений меньше 10.
    /// </summary>
    public static string Size(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < SizeUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        var text = unit == 0
            ? value.ToString("0", CultureInfo.CurrentCulture)
            : value.ToString(value < 10 ? "0.#" : "0", CultureInfo.CurrentCulture);

        return $"{text} {L.T(SizeUnits[unit])}";
    }

    /// <summary>
    /// Доля от 0 до 1 как проценты без дробной части: «43 %».
    /// </summary>
    public static string Percent(double fraction)
    {
        var percent = Math.Clamp((int)Math.Round(fraction * 100), 0, 100);
        return L.F("Format.Percent", percent.ToString(CultureInfo.CurrentCulture));
    }

    /// <summary>
    /// Время относительно <paramref name="now"/>: «только что», «5 мин назад», «2 ч назад», «вчера»,
    /// «3 дн назад», «12 окт», «12.10.2024» для другого года. Будущее время показывается как «только что».
    /// </summary>
    public static string RelativeTime(DateTimeOffset value, DateTimeOffset now)
    {
        var age = now - value;
        if (age < TimeSpan.FromMinutes(1))
        {
            return L.T("Time.JustNow");
        }

        if (age < TimeSpan.FromHours(1))
        {
            return L.F("Time.Ago", Count((int)age.TotalMinutes, L.T("Time.Minute.One"), L.T("Time.Minute.Few"), L.T("Time.Minute.Many")));
        }

        if (age < TimeSpan.FromHours(24) && value.Date == now.Date)
        {
            return L.F("Time.Ago", Count((int)age.TotalHours, L.T("Time.Hour.One"), L.T("Time.Hour.Few"), L.T("Time.Hour.Many")));
        }

        if (value.Date == now.Date.AddDays(-1))
        {
            return L.T("Time.Yesterday");
        }

        if (age < TimeSpan.FromDays(7))
        {
            return L.F("Time.Ago", Count((int)age.TotalDays, L.T("Time.Day.One"), L.T("Time.Day.Few"), L.T("Time.Day.Many")));
        }

        return value.Year == now.Year
            ? $"{value.Day.ToString(CultureInfo.CurrentCulture)} {L.T("Month.Short." + value.Month.ToString(CultureInfo.InvariantCulture))}"
            : value.ToString(L.T("Date.Full"), CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// Число с правильно склонённым словом: «1 файл», «2 файла», «5 файлов», «128 412 файлов».
    /// Разряды разделяются узким пробелом, как в русской типографике.
    /// </summary>
    /// <param name="count">Количество.</param>
    /// <param name="one">Форма для 1, 21, 31…</param>
    /// <param name="few">Форма для 2–4, 22–24…</param>
    /// <param name="many">Форма для 5–20, 25–30…</param>
    public static string Count(long count, string one, string few, string many)
        => $"{Number(count)} {Plural(count, one, few, many)}";

    /// <summary>
    /// Число с разделением разрядов узким неразрывным пробелом: «128 412».
    /// </summary>
    public static string Number(long value)
    {
        if (Localizer.Instance.IsEnglish)
        {
            return value.ToString("N0", Localizer.Instance.Culture);
        }

        var text = Math.Abs(value).ToString(CultureInfo.InvariantCulture);
        var groups = new List<string>();
        for (var end = text.Length; end > 0; end -= 3)
        {
            groups.Insert(0, text[Math.Max(0, end - 3)..end]);
        }

        return (value < 0 ? "−" : string.Empty) + string.Join(' ', groups);
    }

    /// <summary>
    /// Форма слова для числа по правилам языка интерфейса: в русском три формы, в английском две (1 — <paramref name="one"/>,
    /// остальное — <paramref name="many"/>).
    /// </summary>
    public static string Plural(long count, string one, string few, string many)
    {
        var absolute = Math.Abs(count);
        if (Localizer.Instance.IsEnglish)
        {
            return absolute == 1 ? one : many;
        }

        var lastTwo = absolute % 100;
        var last = absolute % 10;

        if (lastTwo is >= 11 and <= 19)
        {
            return many;
        }

        return last switch
        {
            1 => one,
            >= 2 and <= 4 => few,
            _ => many,
        };
    }
}

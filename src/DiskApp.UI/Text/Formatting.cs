using System.Globalization;

namespace DiskApp.UI.Text;

/// <summary>
/// Подписи для интерфейса: размеры, относительное время, количества со склонением.
/// Пока интерфейс только русский; локализация приходит в этапе 9 и заменит строки на ресурсы.
/// </summary>
public static class Formatting
{
    private static readonly string[] SizeUnits = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
    private static readonly string[] MonthsGenitive =
    [
        "янв", "фев", "мар", "апр", "мая", "июн", "июл", "авг", "сен", "окт", "ноя", "дек",
    ];

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

        return $"{text} {SizeUnits[unit]}";
    }

    /// <summary>
    /// Доля от 0 до 1 как проценты без дробной части: «43 %».
    /// </summary>
    public static string Percent(double fraction)
    {
        var percent = Math.Clamp((int)Math.Round(fraction * 100), 0, 100);
        return $"{percent.ToString(CultureInfo.CurrentCulture)} %";
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
            return "только что";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{Count((int)age.TotalMinutes, "минуту", "минуты", "минут")} назад";
        }

        if (age < TimeSpan.FromHours(24) && value.Date == now.Date)
        {
            return $"{Count((int)age.TotalHours, "час", "часа", "часов")} назад";
        }

        if (value.Date == now.Date.AddDays(-1))
        {
            return "вчера";
        }

        if (age < TimeSpan.FromDays(7))
        {
            return $"{Count((int)age.TotalDays, "день", "дня", "дней")} назад";
        }

        return value.Year == now.Year
            ? $"{value.Day.ToString(CultureInfo.CurrentCulture)} {MonthsGenitive[value.Month - 1]}"
            : value.ToString("dd.MM.yyyy", CultureInfo.CurrentCulture);
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
        var text = Math.Abs(value).ToString(CultureInfo.InvariantCulture);
        var groups = new List<string>();
        for (var end = text.Length; end > 0; end -= 3)
        {
            groups.Insert(0, text[Math.Max(0, end - 3)..end]);
        }

        return (value < 0 ? "−" : string.Empty) + string.Join(' ', groups);
    }

    /// <summary>
    /// Форма слова для числа по правилам русского языка.
    /// </summary>
    public static string Plural(long count, string one, string few, string many)
    {
        var absolute = Math.Abs(count);
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

using System.Collections.Frozen;
using System.Globalization;

using DiskApp.Core.Domain;

namespace DiskApp.Core.Search;

/// <summary>
/// Разбор значений фильтров запроса (PLAN.md, этап 3.1): размеры с единицами и сравнениями, даты (абсолютные,
/// относительные, диапазоны) в местном часовом поясе, виды файлов, признаки <c>is:</c> и избранное — на русском
/// и английском. Парсер использует эти методы для проверки, планировщик — для получения границ.
/// </summary>
public static class FilterValues
{
    private static readonly FrozenDictionary<string, FileKind> KindSynonymsMap = BuildKinds();

    private static readonly FrozenDictionary<string, AttributeFilter> AttributeSynonyms = new Dictionary<string, AttributeFilter>(StringComparer.Ordinal)
    {
        ["dir"] = AttributeFilter.Directory,
        ["directory"] = AttributeFilter.Directory,
        ["folder"] = AttributeFilter.Directory,
        ["папка"] = AttributeFilter.Directory,
        ["каталог"] = AttributeFilter.Directory,
        ["file"] = AttributeFilter.File,
        ["файл"] = AttributeFilter.File,
        ["hidden"] = AttributeFilter.Hidden,
        ["скрытый"] = AttributeFilter.Hidden,
        ["скрытые"] = AttributeFilter.Hidden,
        ["fav"] = AttributeFilter.Favorite,
        ["favorite"] = AttributeFilter.Favorite,
        ["избранное"] = AttributeFilter.Favorite,
        ["избранный"] = AttributeFilter.Favorite,
        ["cloud"] = AttributeFilter.CloudPlaceholder,
        ["облако"] = AttributeFilter.CloudPlaceholder,
        ["облачный"] = AttributeFilter.CloudPlaceholder,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly (string Suffix, long Multiplier)[] SizeUnits =
    [
        ("tb", 1L << 40), ("тб", 1L << 40), ("t", 1L << 40), ("т", 1L << 40),
        ("gb", 1L << 30), ("гб", 1L << 30), ("g", 1L << 30), ("г", 1L << 30),
        ("mb", 1L << 20), ("мб", 1L << 20), ("m", 1L << 20), ("м", 1L << 20),
        ("kb", 1L << 10), ("кб", 1L << 10), ("k", 1L << 10), ("к", 1L << 10),
        ("b", 1), ("б", 1),
    ];

    /// <summary>Синонимы видов файлов на русском и английском (свёрнутые).</summary>
    public static IReadOnlyDictionary<string, FileKind> KindSynonyms => KindSynonymsMap;

    /// <summary>Канонические значения <c>is:</c> для подсказок.</summary>
    public static IReadOnlyList<string> AttributeValues { get; } = ["dir", "file", "hidden", "fav", "cloud"];

    /// <summary>
    /// Размер: <c>10mb</c> (от 10 до 11 МБ), <c>&gt;10mb</c>, <c>&gt;=1g</c>, <c>&lt;500kb</c>, <c>&lt;=2гб</c>, <c>1mb..5mb</c>,
    /// <c>empty</c>/<c>пустые</c> (0 байт). Дробная часть через точку или запятую.
    /// </summary>
    public static bool TryParseSize(string value, out SizeRange range)
    {
        range = default;
        var text = SearchText.Fold(value.Trim());
        if (text.Length == 0)
        {
            return false;
        }

        if (text is "empty" or "пустой" or "пустые" or "пусто")
        {
            range = new SizeRange(0, 0);
            return true;
        }

        var dots = text.IndexOf("..", StringComparison.Ordinal);
        if (dots >= 0)
        {
            if (!TryParseSizeAmount(text[..dots], out var from, out _) || !TryParseSizeAmount(text[(dots + 2)..], out var to, out _) || to < from)
            {
                return false;
            }

            range = new SizeRange(from, to);
            return true;
        }

        var (op, rest) = SplitComparison(text);
        if (!TryParseSizeAmount(rest, out var amount, out var unit))
        {
            return false;
        }

        range = op switch
        {
            ">" => new SizeRange(amount + 1, long.MaxValue),
            ">=" => new SizeRange(amount, long.MaxValue),
            "<" => new SizeRange(0, Math.Max(0, amount - 1)),
            "<=" => new SizeRange(0, amount),
            _ => new SizeRange(amount, unit > 1 ? amount + unit - 1 : amount),
        };
        return true;
    }

    /// <summary>
    /// Дата изменения или создания: <c>today</c>/<c>сегодня</c>, <c>yesterday</c>/<c>вчера</c>, <c>week</c>/<c>неделя</c>
    /// (последние 7 дней), <c>month</c>/<c>месяц</c> (30 дней), <c>year</c>/<c>год</c> (365 дней), <c>3d</c>, <c>2w</c>, <c>6m</c>, <c>1y</c>
    /// (и <c>3д</c>, <c>2н</c>, <c>6мес</c>, <c>1г</c>), <c>2026</c>, <c>2026-03</c>, <c>2026-03-05</c>, <c>05.03.2026</c>,
    /// сравнения <c>&gt;</c>, <c>&gt;=</c>, <c>&lt;</c>, <c>&lt;=</c> и диапазоны <c>2026-01..2026-03</c>. Границы дней — в местном поясе.
    /// </summary>
    public static bool TryParseDate(string value, DateTimeOffset now, TimeZoneInfo zone, out DateRange range)
    {
        ArgumentNullException.ThrowIfNull(zone);

        range = default;
        var text = SearchText.Fold(value.Trim());
        if (text.Length == 0)
        {
            return false;
        }

        var dots = text.IndexOf("..", StringComparison.Ordinal);
        if (dots >= 0)
        {
            if (!TryParsePeriod(text[..dots], now, zone, out var from) || !TryParsePeriod(text[(dots + 2)..], now, zone, out var to))
            {
                return false;
            }

            range = new DateRange(from.FromUtc, to.ToUtc);
            return range.FromUtc is null || range.ToUtc is null || range.FromUtc < range.ToUtc;
        }

        var (op, rest) = SplitComparison(text);
        if (!TryParsePeriod(rest, now, zone, out var period))
        {
            return false;
        }

        range = op switch
        {
            ">" => new DateRange(period.ToUtc, null),
            ">=" => new DateRange(period.FromUtc, null),
            "<" => new DateRange(null, period.FromUtc),
            "<=" => new DateRange(null, period.ToUtc),
            _ => period,
        };
        return true;
    }

    /// <summary>
    /// Виды через запятую: <c>image</c>, <c>картинки,видео</c>, <c>doc</c>. Неизвестное слово — ошибка.
    /// </summary>
    public static bool TryParseKinds(string value, out IReadOnlyList<FileKind> kinds)
    {
        var result = new List<FileKind>();
        foreach (var part in SplitList(value))
        {
            if (!KindSynonymsMap.TryGetValue(part, out var kind))
            {
                kinds = [];
                return false;
            }

            if (!result.Contains(kind))
            {
                result.Add(kind);
            }
        }

        kinds = result;
        return result.Count > 0;
    }

    /// <summary>Признак <c>is:</c>: папка, файл, скрытый, избранное, облачная заглушка.</summary>
    public static bool TryParseAttribute(string value, out AttributeFilter attribute)
        => AttributeSynonyms.TryGetValue(SearchText.Fold(value.Trim()), out attribute);

    /// <summary>
    /// Значение <c>fav:</c>: пусто, <c>yes</c>, <c>true</c>, <c>1</c>, <c>да</c> — только избранное; <c>no</c>, <c>false</c>, <c>0</c>, <c>нет</c> — кроме избранного.
    /// </summary>
    public static bool TryParseFavorite(string value, out bool favorite)
    {
        switch (SearchText.Fold(value.Trim()))
        {
            case "" or "yes" or "true" or "1" or "да":
                favorite = true;
                return true;
            case "no" or "false" or "0" or "нет":
                favorite = false;
                return true;
            default:
                favorite = false;
                return false;
        }
    }

    /// <summary>Расширения через запятую без точек, в нижнем регистре.</summary>
    public static IReadOnlyList<string> ParseExtensions(string value)
        => SplitList(value).Select(static part => part.TrimStart('.')).Where(static part => part.Length > 0).Distinct(StringComparer.Ordinal).ToArray();

    private static string[] SplitList(string value)
        => SearchText.Fold(value).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static (string Op, string Remainder) SplitComparison(string text)
        => text switch
        {
            _ when text.StartsWith(">=", StringComparison.Ordinal) => (">=", text[2..]),
            _ when text.StartsWith("<=", StringComparison.Ordinal) => ("<=", text[2..]),
            _ when text.StartsWith('>') => (">", text[1..]),
            _ when text.StartsWith('<') => ("<", text[1..]),
            _ when text.StartsWith('=') => ("=", text[1..]),
            _ => (string.Empty, text),
        };

    private static bool TryParseSizeAmount(string text, out long bytes, out long unit)
    {
        bytes = 0;
        unit = 1;
        var trimmed = text.Trim();
        foreach (var (suffix, multiplier) in SizeUnits)
        {
            if (trimmed.EndsWith(suffix, StringComparison.Ordinal))
            {
                unit = multiplier;
                trimmed = trimmed[..^suffix.Length].TrimEnd();
                break;
            }
        }

        if (!decimal.TryParse(trimmed.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) || number < 0)
        {
            return false;
        }

        var value = number * unit;
        if (value > long.MaxValue)
        {
            return false;
        }

        bytes = (long)decimal.Floor(value);
        return true;
    }

    private static bool TryParsePeriod(string text, DateTimeOffset now, TimeZoneInfo zone, out DateRange period)
    {
        period = default;
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var today = localNow.Date;

        switch (text)
        {
            case "today" or "сегодня":
                period = Days(today, 1, zone);
                return true;
            case "yesterday" or "вчера":
                period = Days(today.AddDays(-1), 1, zone);
                return true;
            case "week" or "неделя" or "неделю":
                period = new DateRange(now.ToUniversalTime().AddDays(-7), null);
                return true;
            case "month" or "месяц":
                period = new DateRange(now.ToUniversalTime().AddDays(-30), null);
                return true;
            case "year" or "год":
                period = new DateRange(now.ToUniversalTime().AddDays(-365), null);
                return true;
            default:
                break;
        }

        if (TryParseRelative(text, out var span))
        {
            period = new DateRange(now.ToUniversalTime() - span, null);
            return true;
        }

        if (text.Length == 4 && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is > 1900 and < 3000)
        {
            var start = new DateTime(year, 1, 1);
            period = new DateRange(ToUtc(start, zone), ToUtc(start.AddYears(1), zone));
            return true;
        }

        if (DateTime.TryParseExact(text, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
        {
            period = new DateRange(ToUtc(month, zone), ToUtc(month.AddMonths(1), zone));
            return true;
        }

        if (DateTime.TryParseExact(text, ["yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            period = Days(day, 1, zone);
            return true;
        }

        return false;
    }

    private static bool TryParseRelative(string text, out TimeSpan span)
    {
        span = default;
        var digits = 0;
        while (digits < text.Length && char.IsAsciiDigit(text[digits]))
        {
            digits++;
        }

        if (digits == 0 || digits == text.Length || !int.TryParse(text.AsSpan(0, digits), NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count <= 0)
        {
            return false;
        }

        var unit = text[digits..];
        double? days = unit switch
        {
            "d" or "day" or "days" or "д" or "дн" or "день" or "дня" or "дней" => 1,
            "w" or "week" or "weeks" or "н" or "нед" or "недели" or "недель" => 7,
            "m" or "month" or "months" or "мес" or "месяц" or "месяца" or "месяцев" => 30,
            "y" or "year" or "years" or "г" or "год" or "года" or "лет" => 365,
            _ => null,
        };

        if (days is null)
        {
            return false;
        }

        span = TimeSpan.FromDays(count * days.Value);
        return true;
    }

    private static DateRange Days(DateTime localStart, int days, TimeZoneInfo zone)
        => new(ToUtc(localStart, zone), ToUtc(localStart.AddDays(days), zone));

    private static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        var offset = zone.IsInvalidTime(unspecified) ? zone.BaseUtcOffset : zone.GetUtcOffset(unspecified);
        return new DateTimeOffset(unspecified, offset).ToUniversalTime();
    }

    private static FrozenDictionary<string, FileKind> BuildKinds()
    {
        (FileKind Kind, string[] Words)[] table =
        [
            (FileKind.Image, ["image", "images", "img", "picture", "pictures", "photo", "photos", "картинка", "картинки", "изображение", "изображения", "фото", "фотографии", "рисунки"]),
            (FileKind.Video, ["video", "videos", "movie", "movies", "видео", "фильм", "фильмы", "ролики"]),
            (FileKind.Audio, ["audio", "music", "sound", "аудио", "музыка", "звук", "песни"]),
            (FileKind.Document, ["doc", "docs", "document", "documents", "text", "документ", "документы", "текст", "тексты"]),
            (FileKind.Spreadsheet, ["sheet", "sheets", "spreadsheet", "spreadsheets", "excel", "таблица", "таблицы"]),
            (FileKind.Presentation, ["presentation", "presentations", "slides", "слайды", "презентация", "презентации"]),
            (FileKind.Pdf, ["pdf"]),
            (FileKind.Archive, ["archive", "archives", "zip", "архив", "архивы"]),
            (FileKind.Code, ["code", "source", "код", "исходники"]),
            (FileKind.Executable, ["exe", "executable", "app", "apps", "program", "programs", "installer", "installers", "программа", "программы", "установщик", "установщики"]),
            (FileKind.Shortcut, ["shortcut", "shortcuts", "link", "links", "lnk", "ярлык", "ярлыки"]),
            (FileKind.Folder, ["folder", "folders", "dir", "directory", "папка", "папки"]),
            (FileKind.Other, ["other", "другое", "прочее"]),
        ];

        var map = new Dictionary<string, FileKind>(StringComparer.Ordinal);
        foreach (var (kind, words) in table)
        {
            foreach (var word in words)
            {
                map[SearchText.Fold(word)] = kind;
            }
        }

        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }
}

/// <summary>
/// Диапазон размеров в байтах, обе границы включительно.
/// </summary>
/// <param name="Min">Наименьший размер.</param>
/// <param name="Max">Наибольший размер; <see cref="long.MaxValue"/> — без верхней границы.</param>
public readonly record struct SizeRange(long Min, long Max);

/// <summary>
/// Диапазон дат в UTC: от <see cref="FromUtc"/> включительно до <see cref="ToUtc"/> не включая; <c>null</c> — без границы.
/// </summary>
/// <param name="FromUtc">Начало.</param>
/// <param name="ToUtc">Конец (не включая).</param>
public readonly record struct DateRange(DateTimeOffset? FromUtc, DateTimeOffset? ToUtc)
{
    /// <summary>Попадает ли момент в диапазон.</summary>
    public bool Contains(DateTimeOffset value) => (FromUtc is null || value >= FromUtc) && (ToUtc is null || value < ToUtc);
}

/// <summary>
/// Признаки фильтра <c>is:</c>.
/// </summary>
public enum AttributeFilter
{
    /// <summary>Папка.</summary>
    Directory,

    /// <summary>Файл.</summary>
    File,

    /// <summary>Скрытый.</summary>
    Hidden,

    /// <summary>В избранном.</summary>
    Favorite,

    /// <summary>Облачная заглушка.</summary>
    CloudPlaceholder,
}

using System.Globalization;

namespace DiskApp.Infrastructure.Sqlite;

/// <summary>
/// Даты в SQLite хранятся текстом ISO 8601 в UTC с семью знаками долей секунды и суффиксом <c>Z</c>:
/// лексикографический порядок совпадает с хронологическим, индексы по датам работают как по числам.
/// </summary>
public static class SqliteTime
{
    private const string Pattern = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    /// <summary>
    /// Текст для записи в базу.
    /// </summary>
    public static string Format(DateTimeOffset value) => value.UtcDateTime.ToString(Pattern, CultureInfo.InvariantCulture);

    /// <summary>
    /// Текст или <c>null</c>.
    /// </summary>
    public static string? Format(DateTimeOffset? value) => value is null ? null : Format(value.Value);

    /// <summary>
    /// Значение из базы.
    /// </summary>
    /// <exception cref="FormatException">Текст не в формате <c>yyyy-MM-ddTHH:mm:ss.fffffffZ</c>.</exception>
    public static DateTimeOffset Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return DateTimeOffset.ParseExact(text, Pattern, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    /// <summary>
    /// Значение из базы или <c>null</c> для <c>NULL</c>.
    /// </summary>
    public static DateTimeOffset? ParseOrNull(string? text) => text is null ? null : Parse(text);
}

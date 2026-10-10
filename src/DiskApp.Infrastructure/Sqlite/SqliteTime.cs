namespace DiskApp.Infrastructure.Sqlite;

/// <summary>
/// Даты в SQLite хранятся целым числом — тиками UTC (<see cref="DateTimeOffset.UtcTicks"/>, 100 нс от 0001-01-01):
/// полная точность времени файловой системы, 8 байт вместо 28 у текста ISO 8601, порядок чисел совпадает
/// с хронологическим (ADR-0007). В SQL перевести в дату: <c>datetime((x - 621355968000000000) / 10000000, 'unixepoch')</c>.
/// </summary>
public static class SqliteTime
{
    /// <summary>
    /// Значение для записи в базу.
    /// </summary>
    public static long Encode(DateTimeOffset value) => value.UtcTicks;

    /// <summary>
    /// Значение для записи или <c>null</c>.
    /// </summary>
    public static long? Encode(DateTimeOffset? value) => value?.UtcTicks;

    /// <summary>
    /// Дата из базы.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Число вне диапазона дат .NET.</exception>
    public static DateTimeOffset Decode(long ticks) => new(ticks, TimeSpan.Zero);

    /// <summary>
    /// Дата из базы или <c>null</c> для <c>NULL</c>.
    /// </summary>
    public static DateTimeOffset? DecodeOrNull(long? ticks) => ticks is null ? null : Decode(ticks.Value);
}

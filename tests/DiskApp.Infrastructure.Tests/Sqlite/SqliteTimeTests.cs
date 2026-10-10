using DiskApp.Infrastructure.Sqlite;

using Dapper;

namespace DiskApp.Infrastructure.Tests.Sqlite;

/// <summary>
/// Даты в базе: формат ISO 8601 UTC, обратимость, лексикографический порядок, чтение и запись через Dapper.
/// </summary>
public sealed class SqliteTimeTests
{
    [Fact]
    public void Format_LocalOffset_ConvertsToUtcWithSevenFractionDigits()
    {
        // Arrange
        var value = new DateTimeOffset(2026, 10, 10, 15, 30, 45, 123, TimeSpan.FromHours(3)).AddTicks(4567);

        // Act
        var text = SqliteTime.Format(value);

        // Assert
        text.ShouldBe("2026-10-10T12:30:45.1234567Z");
        SqliteTime.Parse(text).ShouldBe(value);
        SqliteTime.Parse(text).Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void Format_OrderedValues_ProduceOrderedText()
    {
        // Arrange
        var earlier = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var later = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(-1));

        // Act & Assert
        string.CompareOrdinal(SqliteTime.Format(earlier), SqliteTime.Format(later)).ShouldBeLessThan(0);
        SqliteTime.Format((DateTimeOffset?)null).ShouldBeNull();
        SqliteTime.ParseOrNull(null).ShouldBeNull();
    }

    [Fact]
    public void Dapper_RoundTrip_ReadsAndWritesDateTimeOffset()
    {
        // Arrange
        using var database = new TemporaryDatabase();
        using var connection = new SqliteConnectionFactory(database.FilePath).Open();
        connection.Execute("CREATE TABLE stamps (id INTEGER PRIMARY KEY, at TEXT NOT NULL, maybe TEXT NULL)");
        var at = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

        // Act
        connection.Execute("INSERT INTO stamps (at, maybe) VALUES (@At, @Maybe)", new { At = at, Maybe = (DateTimeOffset?)null });
        var row = connection.QuerySingle<StampRow>("SELECT id, at, maybe FROM stamps");
        var stored = connection.ExecuteScalar<string>("SELECT at FROM stamps");

        // Assert
        row.At.ShouldBe(at);
        row.Maybe.ShouldBeNull();
        stored.ShouldBe("2026-10-10T12:00:00.0000000Z");
    }

    private sealed record StampRow(long Id, DateTimeOffset At, DateTimeOffset? Maybe);
}

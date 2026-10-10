using Dapper;

using DiskApp.Infrastructure.Sqlite;

namespace DiskApp.Infrastructure.Tests.Sqlite;

/// <summary>
/// Даты в базе: тики UTC, полная точность, порядок чисел, чтение и запись через Dapper (ADR-0007).
/// </summary>
public sealed class SqliteTimeTests
{
    [Fact]
    public void Encode_LocalOffset_StoresUtcTicksWithFullPrecision()
    {
        // Arrange
        var value = new DateTimeOffset(2026, 10, 10, 15, 30, 45, 123, TimeSpan.FromHours(3)).AddTicks(4567);

        // Act
        var ticks = SqliteTime.Encode(value);

        // Assert
        ticks.ShouldBe(new DateTime(2026, 10, 10, 12, 30, 45, 123, DateTimeKind.Utc).AddTicks(4567).Ticks);
        SqliteTime.Decode(ticks).ShouldBe(value);
        SqliteTime.Decode(ticks).Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void Encode_OrderedValues_ProduceOrderedNumbers()
    {
        // Arrange
        var earlier = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var later = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(-1));

        // Act & Assert
        SqliteTime.Encode(earlier).ShouldBeLessThan(SqliteTime.Encode(later));
        SqliteTime.Encode((DateTimeOffset?)null).ShouldBeNull();
        SqliteTime.DecodeOrNull(null).ShouldBeNull();
    }

    [Fact]
    public void Dapper_RoundTrip_ReadsAndWritesDateTimeOffset()
    {
        // Arrange
        using var database = new TemporaryDatabase();
        using var connection = new SqliteConnectionFactory(database.FilePath).Open();
        connection.Execute("CREATE TABLE stamps (id INTEGER PRIMARY KEY, at INTEGER NOT NULL, maybe INTEGER NULL)");
        var at = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

        // Act
        connection.Execute("INSERT INTO stamps (at, maybe) VALUES (@At, @Maybe)", new { At = at, Maybe = (DateTimeOffset?)null });
        var row = connection.QuerySingle<StampRow>("SELECT id, at, maybe FROM stamps");
        var stored = connection.ExecuteScalar<long>("SELECT at FROM stamps");
        var storedType = connection.ExecuteScalar<string>("SELECT typeof(at) FROM stamps");
        var readable = connection.ExecuteScalar<string>("SELECT datetime((at - 621355968000000000) / 10000000, 'unixepoch') FROM stamps");

        // Assert
        row.At.ShouldBe(at);
        row.Maybe.ShouldBeNull();
        stored.ShouldBe(at.UtcTicks);
        storedType.ShouldBe("integer");
        readable.ShouldBe("2026-10-10 12:00:00");
    }

    private sealed record StampRow(long Id, DateTimeOffset At, DateTimeOffset? Maybe);
}

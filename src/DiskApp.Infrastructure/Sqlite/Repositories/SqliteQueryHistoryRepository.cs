using Dapper;

using DiskApp.Core.Repositories;

namespace DiskApp.Infrastructure.Sqlite.Repositories;

/// <summary>
/// История запросов в SQLite. Подсказки по префиксу фильтруются в памяти без учёта регистра:
/// история короткая, а <c>LIKE</c> в SQLite не сворачивает регистр кириллицы.
/// </summary>
public sealed class SqliteQueryHistoryRepository(SqliteDatabase database) : SqliteRepository(database), IQueryHistoryRepository
{
    private const int SuggestScanLimit = 500;
    private const string Columns = "query, use_count, last_used_utc";

    /// <inheritdoc />
    public Task RecordAsync(string query, DateTimeOffset usedAtUtc, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var trimmed = query.Trim();

        return WriteAsync(connection => connection.Execute(
            """
            INSERT INTO query_history (query, use_count, last_used_utc) VALUES (@Query, 1, @UsedAt)
            ON CONFLICT (query) DO UPDATE SET use_count = use_count + 1, last_used_utc = excluded.last_used_utc
            """,
            new { Query = trimmed, UsedAt = SqliteTime.Format(usedAtUtc) }), cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<QueryHistoryEntry>> GetRecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        return ReadAsync<IReadOnlyList<QueryHistoryEntry>>(connection => connection
            .Query<HistoryRow>($"SELECT {Columns} FROM query_history ORDER BY last_used_utc DESC LIMIT @Limit", new { Limit = limit })
            .Select(static row => row.ToEntry())
            .ToArray(), cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<QueryHistoryEntry>> SuggestAsync(string prefix, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var trimmed = prefix.Trim();

        return ReadAsync<IReadOnlyList<QueryHistoryEntry>>(connection => connection
            .Query<HistoryRow>($"SELECT {Columns} FROM query_history ORDER BY last_used_utc DESC LIMIT @Limit", new { Limit = SuggestScanLimit })
            .Where(row => row.Query.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .Select(static row => row.ToEntry())
            .ToArray(), cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> RemoveAsync(string query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var trimmed = query.Trim();

        return WriteAsync(connection => connection.Execute("DELETE FROM query_history WHERE query = @Query", new { Query = trimmed }) == 1, cancellationToken);
    }

    /// <inheritdoc />
    public Task ClearAsync(CancellationToken cancellationToken = default)
        => WriteAsync(connection => connection.Execute("DELETE FROM query_history"), cancellationToken);

    private sealed class HistoryRow
    {
        public string Query { get; set; } = string.Empty;

        public long UseCount { get; set; }

        public string LastUsedUtc { get; set; } = string.Empty;

        public QueryHistoryEntry ToEntry() => new(Query, checked((int)UseCount), SqliteTime.Parse(LastUsedUtc));
    }
}

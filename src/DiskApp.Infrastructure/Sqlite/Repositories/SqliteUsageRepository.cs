using Dapper;

using DiskApp.Core.Domain;
using DiskApp.Core.Repositories;

namespace DiskApp.Infrastructure.Sqlite.Repositories;

/// <summary>
/// Статистика использования в SQLite: одна строка на запись, инкремент через <c>ON CONFLICT DO UPDATE</c>.
/// </summary>
public sealed class SqliteUsageRepository(SqliteDatabase database) : SqliteRepository(database), IUsageRepository
{
    private const string Columns = "entry_id, open_count, last_opened_utc";

    /// <inheritdoc />
    public Task RecordOpenAsync(long entryId, DateTimeOffset openedAtUtc, CancellationToken cancellationToken = default)
        => WriteAsync(connection => connection.Execute(
            """
            INSERT INTO usage (entry_id, open_count, last_opened_utc) VALUES (@EntryId, 1, @OpenedAt)
            ON CONFLICT (entry_id) DO UPDATE SET open_count = open_count + 1, last_opened_utc = excluded.last_opened_utc
            """,
            new { EntryId = entryId, OpenedAt = SqliteTime.Encode(openedAtUtc) }), cancellationToken);

    /// <inheritdoc />
    public Task<UsageStat?> GetAsync(long entryId, CancellationToken cancellationToken = default)
        => ReadAsync(connection => connection
            .QuerySingleOrDefault<UsageRow>($"SELECT {Columns} FROM usage WHERE entry_id = @EntryId", new { EntryId = entryId })?
            .ToStat(), cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<UsageStat>> GetManyAsync(IReadOnlyList<long> entryIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        if (entryIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<UsageStat>>([]);
        }

        return ReadAsync<IReadOnlyList<UsageStat>>(connection =>
        {
            var result = new List<UsageStat>(entryIds.Count);
            foreach (var chunk in entryIds.Chunk(500))
            {
                result.AddRange(connection
                    .Query<UsageRow>($"SELECT {Columns} FROM usage WHERE entry_id IN @Ids", new { Ids = chunk })
                    .Select(static row => row.ToStat()));
            }

            return result;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<UsageStat>> GetRecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        return ReadAsync<IReadOnlyList<UsageStat>>(connection => connection
            .Query<UsageRow>($"SELECT {Columns} FROM usage WHERE last_opened_utc IS NOT NULL ORDER BY last_opened_utc DESC LIMIT @Limit", new { Limit = limit })
            .Select(static row => row.ToStat())
            .ToArray(), cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveAsync(long entryId, CancellationToken cancellationToken = default)
        => WriteAsync(connection => connection.Execute("DELETE FROM usage WHERE entry_id = @EntryId", new { EntryId = entryId }), cancellationToken);

    private sealed class UsageRow
    {
        public long EntryId { get; set; }

        public long OpenCount { get; set; }

        public long? LastOpenedUtc { get; set; }

        public UsageStat ToStat() => new(EntryId, checked((int)OpenCount), SqliteTime.DecodeOrNull(LastOpenedUtc));
    }
}

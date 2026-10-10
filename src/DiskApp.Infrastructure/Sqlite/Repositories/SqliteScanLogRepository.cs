using Dapper;

using DiskApp.Core.Repositories;

namespace DiskApp.Infrastructure.Sqlite.Repositories;

/// <summary>
/// Журнал сканирования в таблице <c>scan_log</c>; записи удаляются каскадно вместе с корнем.
/// </summary>
public sealed class SqliteScanLogRepository(SqliteDatabase database) : SqliteRepository(database), IScanLogRepository
{
    /// <inheritdoc />
    public Task AddAsync(IReadOnlyList<ScanLogEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return Task.CompletedTask;
        }

        return WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            connection.Execute(
                "INSERT INTO scan_log (root_id, scan_id, occurred_utc, level, path, message) VALUES (@RootId, @ScanId, @OccurredUtc, @Level, @Path, @Message)",
                entries.Select(static entry => new
                {
                    entry.RootId,
                    entry.ScanId,
                    OccurredUtc = SqliteTime.Encode(entry.OccurredUtc),
                    Level = (int)entry.Level,
                    entry.Path,
                    entry.Message,
                }),
                transaction);
            transaction.Commit();
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ScanLogEntry>> GetRecentAsync(long rootId, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        return ReadAsync<IReadOnlyList<ScanLogEntry>>(connection => connection
            .Query<ScanLogRow>(
                "SELECT id, root_id, scan_id, occurred_utc, level, path, message FROM scan_log WHERE root_id = @RootId ORDER BY id DESC LIMIT @Limit",
                new { RootId = rootId, Limit = limit })
            .Select(static row => row.ToEntry())
            .ToArray(), cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> PruneAsync(long rootId, int keep, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(keep);

        return WriteAsync(connection => connection.Execute(
            """
            DELETE FROM scan_log WHERE root_id = @RootId AND id NOT IN (
                SELECT id FROM scan_log WHERE root_id = @RootId ORDER BY id DESC LIMIT @Keep)
            """,
            new { RootId = rootId, Keep = keep }), cancellationToken);
    }

    private sealed class ScanLogRow
    {
        public long Id { get; set; }

        public long RootId { get; set; }

        public long ScanId { get; set; }

        public long OccurredUtc { get; set; }

        public long Level { get; set; }

        public string? Path { get; set; }

        public string Message { get; set; } = string.Empty;

        public ScanLogEntry ToEntry() => new(RootId, ScanId, SqliteTime.Decode(OccurredUtc), (ScanLogLevel)Level, Path, Message) { Id = Id };
    }
}

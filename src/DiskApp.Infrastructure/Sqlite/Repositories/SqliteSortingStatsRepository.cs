using Dapper;

using DiskApp.Core.Repositories;
using DiskApp.Core.Sorting;

namespace DiskApp.Infrastructure.Sqlite.Repositories;

/// <summary>
/// История решений разбора в SQLite (PLAN.md, этап 17.4): таблица <c>sorting_stats</c> — признак имени, папка, вес с
/// затуханием, число решений и время последнего. Читается целиком один раз: служба подсказок держит её в памяти.
/// </summary>
public sealed class SqliteSortingStatsRepository(SqliteDatabase database) : SqliteRepository(database), ISortingStatsRepository
{
    /// <inheritdoc />
    public Task<IReadOnlyList<SortingStat>> GetAllAsync(CancellationToken cancellationToken = default)
        => ReadAsync<IReadOnlyList<SortingStat>>(
            connection => [.. connection
                .Query<StatRow>("SELECT feature AS Feature, destination AS Destination, score AS Score, count AS Count, updated_utc AS Updated FROM sorting_stats")
                .Select(static row => new SortingStat(row.Feature, row.Destination, row.Score, (int)row.Count, SqliteTime.Decode(row.Updated)))],
            cancellationToken);

    /// <inheritdoc />
    public Task SaveAsync(IReadOnlyList<SortingStat> upserts, IReadOnlyList<(string Feature, string Destination)> deletes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upserts);
        ArgumentNullException.ThrowIfNull(deletes);
        if (upserts.Count == 0 && deletes.Count == 0)
        {
            return Task.CompletedTask;
        }

        return WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            foreach (var (feature, destination) in deletes)
            {
                connection.Execute("DELETE FROM sorting_stats WHERE feature = @Feature AND destination = @Destination", new { Feature = feature, Destination = destination }, transaction);
            }

            foreach (var stat in upserts)
            {
                connection.Execute(
                    """
                    INSERT INTO sorting_stats (feature, destination, score, count, updated_utc) VALUES (@Feature, @Destination, @Score, @Count, @Updated)
                    ON CONFLICT (feature, destination) DO UPDATE SET score = excluded.score, count = excluded.count, updated_utc = excluded.updated_utc
                    """,
                    new { stat.Feature, stat.Destination, stat.Score, stat.Count, Updated = SqliteTime.Encode(stat.UpdatedUtc) },
                    transaction);
            }

            transaction.Commit();
        }, cancellationToken);
    }

    private sealed class StatRow
    {
        public string Feature { get; set; } = string.Empty;

        public string Destination { get; set; } = string.Empty;

        public double Score { get; set; }

        public long Count { get; set; }

        public long Updated { get; set; }
    }
}

using Dapper;

using DiskApp.Core.Domain;
using DiskApp.Core.Repositories;

namespace DiskApp.Infrastructure.Sqlite.Repositories;

/// <summary>
/// Избранное в SQLite: порядок хранится в <c>sort_order</c>, новая запись становится последней.
/// </summary>
public sealed class SqliteFavoriteRepository(SqliteDatabase database) : SqliteRepository(database), IFavoriteRepository
{
    private const string Columns = "entry_id, sort_order, pinned_utc";

    /// <inheritdoc />
    public Task<IReadOnlyList<Favorite>> GetAllAsync(CancellationToken cancellationToken = default)
        => ReadAsync<IReadOnlyList<Favorite>>(connection => connection
            .Query<FavoriteRow>($"SELECT {Columns} FROM favorites ORDER BY sort_order, entry_id")
            .Select(static row => row.ToFavorite())
            .ToArray(), cancellationToken);

    /// <inheritdoc />
    public Task<bool> ContainsAsync(long entryId, CancellationToken cancellationToken = default)
        => ReadAsync(connection => connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM favorites WHERE entry_id = @EntryId", new { EntryId = entryId }) > 0, cancellationToken);

    /// <inheritdoc />
    public Task<Favorite> AddAsync(long entryId, DateTimeOffset pinnedAtUtc, CancellationToken cancellationToken = default)
        => WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            connection.Execute(
                """
                INSERT OR IGNORE INTO favorites (entry_id, sort_order, pinned_utc)
                VALUES (@EntryId, (SELECT COALESCE(MAX(sort_order), -1) + 1 FROM favorites), @PinnedAt)
                """,
                new { EntryId = entryId, PinnedAt = SqliteTime.Format(pinnedAtUtc) },
                transaction);
            var row = connection.QuerySingle<FavoriteRow>($"SELECT {Columns} FROM favorites WHERE entry_id = @EntryId", new { EntryId = entryId }, transaction);
            transaction.Commit();
            return row.ToFavorite();
        }, cancellationToken);

    /// <inheritdoc />
    public Task<bool> RemoveAsync(long entryId, CancellationToken cancellationToken = default)
        => WriteAsync(connection => connection.Execute("DELETE FROM favorites WHERE entry_id = @EntryId", new { EntryId = entryId }) == 1, cancellationToken);

    /// <inheritdoc />
    public Task ReorderAsync(IReadOnlyList<long> entryIdsInOrder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entryIdsInOrder);

        return WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var current = connection.Query<long>("SELECT entry_id FROM favorites ORDER BY sort_order, entry_id", transaction: transaction).ToList();
            var ordered = entryIdsInOrder.Where(current.Contains).Distinct().ToList();
            ordered.AddRange(current.Where(id => !ordered.Contains(id)));

            for (var i = 0; i < ordered.Count; i++)
            {
                connection.Execute("UPDATE favorites SET sort_order = @Order WHERE entry_id = @EntryId", new { Order = i, EntryId = ordered[i] }, transaction);
            }

            transaction.Commit();
        }, cancellationToken);
    }

    private sealed class FavoriteRow
    {
        public long EntryId { get; set; }

        public long SortOrder { get; set; }

        public string PinnedUtc { get; set; } = string.Empty;

        public Favorite ToFavorite() => new(EntryId, checked((int)SortOrder), SqliteTime.Parse(PinnedUtc));
    }
}

using Dapper;

using DiskApp.Core.Abstractions;
using DiskApp.Core.Domain;
using DiskApp.Core.Repositories;

namespace DiskApp.Infrastructure.Sqlite.Repositories;

/// <summary>
/// Коллекции в SQLite: элементы с порядком в <c>collection_items</c>, каскадное удаление вместе с коллекцией и записью.
/// </summary>
public sealed class SqliteCollectionRepository(SqliteDatabase database, IClock clock) : SqliteRepository(database), ICollectionRepository
{
    private const string Columns = "id, name, kind, query";

    /// <inheritdoc />
    public Task<IReadOnlyList<Collection>> GetAllAsync(CancellationToken cancellationToken = default)
        => ReadAsync<IReadOnlyList<Collection>>(connection => connection
            .Query<CollectionRow>($"SELECT {Columns} FROM collections ORDER BY name, id")
            .Select(static row => row.ToCollection())
            .ToArray(), cancellationToken);

    /// <inheritdoc />
    public Task<Collection?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
        => ReadAsync(connection => connection
            .QuerySingleOrDefault<CollectionRow>($"SELECT {Columns} FROM collections WHERE id = @Id", new { Id = id })?
            .ToCollection(), cancellationToken);

    /// <inheritdoc />
    public Task<Collection> AddAsync(Collection collection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(collection.Name);
        var createdUtc = SqliteTime.Encode(clock.UtcNow);

        return WriteAsync(connection =>
        {
            var id = connection.ExecuteScalar<long>(
                "INSERT INTO collections (name, kind, query, created_utc) VALUES (@Name, @Kind, @Query, @CreatedUtc); SELECT last_insert_rowid();",
                new { collection.Name, Kind = (int)collection.Kind, collection.Query, CreatedUtc = createdUtc });
            return collection with { Id = id };
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> UpdateAsync(Collection collection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(collection.Name);

        return WriteAsync(connection => connection.Execute(
            "UPDATE collections SET name = @Name, kind = @Kind, query = @Query WHERE id = @Id",
            new { collection.Id, collection.Name, Kind = (int)collection.Kind, collection.Query }) == 1, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default)
        => WriteAsync(connection => connection.Execute("DELETE FROM collections WHERE id = @Id", new { Id = id }) == 1, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<long>> GetItemsAsync(long collectionId, CancellationToken cancellationToken = default)
        => ReadAsync<IReadOnlyList<long>>(connection => connection
            .Query<long>("SELECT entry_id FROM collection_items WHERE collection_id = @CollectionId ORDER BY sort_order, entry_id", new { CollectionId = collectionId })
            .ToArray(), cancellationToken);

    /// <inheritdoc />
    public Task AddItemAsync(long collectionId, long entryId, CancellationToken cancellationToken = default)
        => WriteAsync(connection => connection.Execute(
            """
            INSERT OR IGNORE INTO collection_items (collection_id, entry_id, sort_order)
            VALUES (@CollectionId, @EntryId, (SELECT COALESCE(MAX(sort_order), -1) + 1 FROM collection_items WHERE collection_id = @CollectionId))
            """,
            new { CollectionId = collectionId, EntryId = entryId }), cancellationToken);

    /// <inheritdoc />
    public Task<bool> RemoveItemAsync(long collectionId, long entryId, CancellationToken cancellationToken = default)
        => WriteAsync(connection => connection.Execute(
            "DELETE FROM collection_items WHERE collection_id = @CollectionId AND entry_id = @EntryId",
            new { CollectionId = collectionId, EntryId = entryId }) == 1, cancellationToken);

    /// <inheritdoc />
    public Task ReorderItemsAsync(long collectionId, IReadOnlyList<long> entryIdsInOrder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entryIdsInOrder);

        return WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var current = connection.Query<long>(
                "SELECT entry_id FROM collection_items WHERE collection_id = @CollectionId ORDER BY sort_order, entry_id",
                new { CollectionId = collectionId }, transaction).ToList();
            var ordered = entryIdsInOrder.Where(current.Contains).Distinct().ToList();
            ordered.AddRange(current.Where(id => !ordered.Contains(id)));

            for (var i = 0; i < ordered.Count; i++)
            {
                connection.Execute(
                    "UPDATE collection_items SET sort_order = @Order WHERE collection_id = @CollectionId AND entry_id = @EntryId",
                    new { Order = i, CollectionId = collectionId, EntryId = ordered[i] }, transaction);
            }

            transaction.Commit();
        }, cancellationToken);
    }

    private sealed class CollectionRow
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public long Kind { get; set; }

        public string? Query { get; set; }

        public Collection ToCollection() => new(Id, Name, (CollectionKind)Kind, Query);
    }
}

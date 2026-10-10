using Dapper;

using DiskApp.Core.Domain;
using DiskApp.Core.Repositories;

namespace DiskApp.Infrastructure.Sqlite.Repositories;

/// <summary>
/// Теги в SQLite: уникальность по <c>name_folded</c> (нижний регистр по правилам .NET), привязки в <c>entry_tags</c>.
/// </summary>
public sealed class SqliteTagRepository(SqliteDatabase database) : SqliteRepository(database), ITagRepository
{
    private const string Columns = "id, name, color";

    /// <inheritdoc />
    public Task<IReadOnlyList<Tag>> GetAllAsync(CancellationToken cancellationToken = default)
        => ReadAsync<IReadOnlyList<Tag>>(connection => connection
            .Query<TagRow>($"SELECT {Columns} FROM tags ORDER BY name_folded, id")
            .Select(static row => row.ToTag())
            .ToArray(), cancellationToken);

    /// <inheritdoc />
    public Task<Tag?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
        => ReadAsync(connection => connection
            .QuerySingleOrDefault<TagRow>($"SELECT {Columns} FROM tags WHERE id = @Id", new { Id = id })?
            .ToTag(), cancellationToken);

    /// <inheritdoc />
    public Task<Tag?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var folded = Fold(name);

        return ReadAsync(connection => connection
            .QuerySingleOrDefault<TagRow>($"SELECT {Columns} FROM tags WHERE name_folded = @Folded", new { Folded = folded })?
            .ToTag(), cancellationToken);
    }

    /// <inheritdoc />
    public Task<Tag> AddAsync(string name, string? color = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.Trim();
        var folded = Fold(trimmed);

        return WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            connection.Execute(
                "INSERT INTO tags (name, name_folded, color) VALUES (@Name, @Folded, @Color) ON CONFLICT (name_folded) DO NOTHING",
                new { Name = trimmed, Folded = folded, Color = color },
                transaction);
            var row = connection.QuerySingle<TagRow>($"SELECT {Columns} FROM tags WHERE name_folded = @Folded", new { Folded = folded }, transaction);
            transaction.Commit();
            return row.ToTag();
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> UpdateAsync(Tag tag, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tag);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag.Name);
        var trimmed = tag.Name.Trim();

        return WriteAsync(connection => connection.Execute(
            "UPDATE tags SET name = @Name, name_folded = @Folded, color = @Color WHERE id = @Id",
            new { tag.Id, Name = trimmed, Folded = Fold(trimmed), tag.Color }) == 1, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default)
        => WriteAsync(connection => connection.Execute("DELETE FROM tags WHERE id = @Id", new { Id = id }) == 1, cancellationToken);

    /// <inheritdoc />
    public Task AssignAsync(long entryId, long tagId, CancellationToken cancellationToken = default)
        => WriteAsync(connection => connection.Execute(
            "INSERT OR IGNORE INTO entry_tags (entry_id, tag_id) VALUES (@EntryId, @TagId)",
            new { EntryId = entryId, TagId = tagId }), cancellationToken);

    /// <inheritdoc />
    public Task<bool> UnassignAsync(long entryId, long tagId, CancellationToken cancellationToken = default)
        => WriteAsync(connection => connection.Execute(
            "DELETE FROM entry_tags WHERE entry_id = @EntryId AND tag_id = @TagId",
            new { EntryId = entryId, TagId = tagId }) == 1, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<Tag>> GetTagsOfEntryAsync(long entryId, CancellationToken cancellationToken = default)
        => ReadAsync<IReadOnlyList<Tag>>(connection => connection
            .Query<TagRow>(
                "SELECT t.id, t.name, t.color FROM tags AS t JOIN entry_tags AS et ON et.tag_id = t.id WHERE et.entry_id = @EntryId ORDER BY t.name_folded, t.id",
                new { EntryId = entryId })
            .Select(static row => row.ToTag())
            .ToArray(), cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<long, IReadOnlyList<Tag>>> GetTagsOfEntriesAsync(IReadOnlyList<long> entryIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        if (entryIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyDictionary<long, IReadOnlyList<Tag>>>(new Dictionary<long, IReadOnlyList<Tag>>());
        }

        return ReadAsync<IReadOnlyDictionary<long, IReadOnlyList<Tag>>>(connection =>
        {
            var groups = new Dictionary<long, List<Tag>>();
            foreach (var chunk in entryIds.Chunk(500))
            {
                var rows = connection.Query<EntryTagRow>(
                    "SELECT et.entry_id, t.id, t.name, t.color FROM tags AS t JOIN entry_tags AS et ON et.tag_id = t.id WHERE et.entry_id IN @Ids ORDER BY t.name_folded, t.id",
                    new { Ids = chunk });
                foreach (var row in rows)
                {
                    if (!groups.TryGetValue(row.EntryId, out var list))
                    {
                        list = [];
                        groups[row.EntryId] = list;
                    }

                    list.Add(new Tag(row.Id, row.Name, row.Color));
                }
            }

            return groups.ToDictionary(static pair => pair.Key, static pair => (IReadOnlyList<Tag>)pair.Value);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<long>> GetEntryIdsWithTagAsync(long tagId, CancellationToken cancellationToken = default)
        => ReadAsync<IReadOnlyList<long>>(connection => connection
            .Query<long>("SELECT entry_id FROM entry_tags WHERE tag_id = @TagId ORDER BY entry_id", new { TagId = tagId })
            .ToArray(), cancellationToken);

    private static string Fold(string name) => name.Trim().ToLowerInvariant();

    private sealed class TagRow
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string? Color { get; set; }

        public Tag ToTag() => new(Id, Name, Color);
    }

    private sealed class EntryTagRow
    {
        public long EntryId { get; set; }

        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string? Color { get; set; }
    }
}

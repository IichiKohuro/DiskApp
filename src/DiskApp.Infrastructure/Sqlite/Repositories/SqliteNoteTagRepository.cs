using Dapper;

using DiskApp.Core.Domain;
using DiskApp.Core.Repositories;
using DiskApp.Core.Search;

namespace DiskApp.Infrastructure.Sqlite.Repositories;

/// <summary>
/// Теги заметок в SQLite (PLAN.md, этап 23): таблица <c>note_tags</c>, уникальность по <c>name_folded</c>, как у тегов
/// файлов, но список свой.
/// </summary>
public sealed class SqliteNoteTagRepository(SqliteDatabase database) : SqliteRepository(database), INoteTagRepository
{
    private const string Columns = "id AS Id, name AS Name, color AS Color";

    /// <inheritdoc />
    public Task<IReadOnlyList<Tag>> GetAllAsync(CancellationToken cancellationToken = default)
        => ReadAsync<IReadOnlyList<Tag>>(connection => connection
            .Query<TagRow>($"SELECT {Columns} FROM note_tags ORDER BY name_folded, id")
            .Select(static row => row.ToTag())
            .ToArray(), cancellationToken);

    /// <inheritdoc />
    public Task<Tag> AddAsync(string name, string? color = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.Trim();
        var folded = SearchText.Fold(trimmed);

        return WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            connection.Execute(
                "INSERT INTO note_tags (name, name_folded, color) VALUES (@Name, @Folded, @Color) ON CONFLICT (name_folded) DO NOTHING",
                new { Name = trimmed, Folded = folded, Color = color },
                transaction);
            var row = connection.QuerySingle<TagRow>($"SELECT {Columns} FROM note_tags WHERE name_folded = @Folded", new { Folded = folded }, transaction);
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
            "UPDATE note_tags SET name = @Name, name_folded = @Folded, color = @Color WHERE id = @Id",
            new { tag.Id, Name = trimmed, Folded = SearchText.Fold(trimmed), tag.Color }) == 1, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default)
        => WriteAsync(connection => connection.Execute("DELETE FROM note_tags WHERE id = @Id", new { Id = id }) == 1, cancellationToken);

    private sealed class TagRow
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string? Color { get; set; }

        public Tag ToTag() => new(Id, Name, Color);
    }
}

using Dapper;

using DiskApp.Core.Domain;
using DiskApp.Core.Repositories;
using DiskApp.Core.Search;

using Microsoft.Data.Sqlite;

namespace DiskApp.Infrastructure.Sqlite.Repositories;

/// <summary>
/// Заметки в SQLite (PLAN.md, этап 23, ADR-0014). Поиск — <c>instr</c> по свёрнутым заголовку и тексту
/// (<see cref="SearchText.Fold"/>): регистр и «ё» не важны, находятся и 1–2 символа. Теги заметок — <c>note_tag_links</c>.
/// </summary>
public sealed class SqliteNoteRepository(SqliteDatabase database) : SqliteRepository(database), INoteRepository
{
    private const string Columns = "n.id AS Id, n.title AS Title, n.body AS Body, n.is_pinned AS IsPinned, n.created_utc AS CreatedUtc, n.modified_utc AS ModifiedUtc, n.deleted_utc AS DeletedUtc, n.project_id AS ProjectId";

    /// <inheritdoc />
    public Task<IReadOnlyList<Note>> ListAsync(NoteQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var where = new List<string> { query.Deleted ? "n.deleted_utc IS NOT NULL" : "n.deleted_utc IS NULL" };
        var parameters = new DynamicParameters();
        var terms = string.IsNullOrWhiteSpace(query.Text)
            ? []
            : SearchText.Fold(query.Text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < terms.Length; i++)
        {
            where.Add($"instr(n.search_folded, @T{i}) > 0");
            parameters.Add($"T{i}", terms[i]);
        }

        if (query.ProjectId is { } projectId)
        {
            where.Add("n.project_id = @ProjectId");
            parameters.Add("ProjectId", projectId);
        }

        if (query.TagId is { } tagId)
        {
            where.Add("EXISTS (SELECT 1 FROM note_tag_links AS l WHERE l.note_id = n.id AND l.tag_id = @TagId)");
            parameters.Add("TagId", tagId);
        }

        parameters.Add("Limit", Math.Clamp(query.Limit, 1, 5_000));
        var order = query.Deleted ? "n.deleted_utc DESC, n.id DESC" : "n.is_pinned DESC, n.modified_utc DESC, n.id DESC";
        var sql = $"SELECT {Columns} FROM notes AS n WHERE {string.Join(" AND ", where)} ORDER BY {order} LIMIT @Limit";

        return ReadAsync<IReadOnlyList<Note>>(connection =>
        {
            var rows = connection.Query<NoteRow>(sql, parameters).ToArray();
            var tags = TagsOf(connection, null, [.. rows.Select(static row => row.Id)]);
            return [.. rows.Select(row => row.ToNote(tags.GetValueOrDefault(row.Id) ?? []))];
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Note?> GetAsync(long id, CancellationToken cancellationToken = default)
        => ReadAsync(connection => Get(connection, null, id), cancellationToken);

    /// <inheritdoc />
    public Task<Note> AddAsync(string body, bool isPinned, IReadOnlyList<long> tagIds, DateTimeOffset nowUtc, long? projectId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(tagIds);

        return WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var now = SqliteTime.Encode(nowUtc);
            var id = connection.ExecuteScalar<long>(
                "INSERT INTO notes (title, body, search_folded, is_pinned, created_utc, modified_utc, project_id) VALUES (@Title, @Body, @Folded, @Pinned, @Now, @Now, @Project) RETURNING id",
                new { Title = NoteText.TitleOf(body), Body = body, Folded = Fold(body), Pinned = isPinned ? 1 : 0, Now = now, Project = projectId },
                transaction);
            SetTags(connection, transaction, id, tagIds);
            var note = Get(connection, transaction, id)!;
            transaction.Commit();
            return note;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Note?> UpdateAsync(long id, string body, bool isPinned, IReadOnlyList<long> tagIds, DateTimeOffset nowUtc, long? projectId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(tagIds);

        return WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var changed = connection.Execute(
                "UPDATE notes SET title = @Title, body = @Body, search_folded = @Folded, is_pinned = @Pinned, modified_utc = @Now, project_id = @Project WHERE id = @Id AND deleted_utc IS NULL",
                new { Id = id, Title = NoteText.TitleOf(body), Body = body, Folded = Fold(body), Pinned = isPinned ? 1 : 0, Now = SqliteTime.Encode(nowUtc), Project = projectId },
                transaction);
            if (changed == 0)
            {
                return null;
            }

            SetTags(connection, transaction, id, tagIds);
            var note = Get(connection, transaction, id);
            transaction.Commit();
            return note;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> TrashAsync(long id, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
        => WriteAsync(connection => connection.Execute(
            "UPDATE notes SET deleted_utc = @Now WHERE id = @Id AND deleted_utc IS NULL",
            new { Id = id, Now = SqliteTime.Encode(nowUtc) }) == 1, cancellationToken);

    /// <inheritdoc />
    public Task<bool> RestoreAsync(long id, CancellationToken cancellationToken = default)
        => WriteAsync(connection => connection.Execute("UPDATE notes SET deleted_utc = NULL WHERE id = @Id AND deleted_utc IS NOT NULL", new { Id = id }) == 1, cancellationToken);

    /// <inheritdoc />
    public Task<bool> PurgeAsync(long id, CancellationToken cancellationToken = default)
        => WriteAsync(connection => connection.Execute("DELETE FROM notes WHERE id = @Id AND deleted_utc IS NOT NULL", new { Id = id }) == 1, cancellationToken);

    /// <inheritdoc />
    public Task<int> PurgeDeletedBeforeAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default)
        => WriteAsync(connection => connection.Execute("DELETE FROM notes WHERE deleted_utc IS NOT NULL AND deleted_utc < @Cutoff", new { Cutoff = SqliteTime.Encode(cutoffUtc) }), cancellationToken);

    private static string Fold(string body) => SearchText.Fold(NoteText.TitleOf(body) + "\n" + body);

    private static Note? Get(SqliteConnection connection, SqliteTransaction? transaction, long id)
    {
        var row = connection.QuerySingleOrDefault<NoteRow>($"SELECT {Columns} FROM notes AS n WHERE n.id = @Id", new { Id = id }, transaction);
        return row?.ToNote(TagsOf(connection, transaction, [id]).GetValueOrDefault(id) ?? []);
    }

    private static void SetTags(SqliteConnection connection, SqliteTransaction transaction, long noteId, IReadOnlyList<long> tagIds)
    {
        connection.Execute("DELETE FROM note_tag_links WHERE note_id = @Id", new { Id = noteId }, transaction);
        foreach (var tagId in tagIds.Distinct())
        {
            connection.Execute(
                "INSERT OR IGNORE INTO note_tag_links (note_id, tag_id) SELECT @Note, id FROM note_tags WHERE id = @Tag",
                new { Note = noteId, Tag = tagId },
                transaction);
        }
    }

    private static Dictionary<long, List<Tag>> TagsOf(SqliteConnection connection, SqliteTransaction? transaction, IReadOnlyList<long> noteIds)
    {
        var result = new Dictionary<long, List<Tag>>();
        foreach (var chunk in noteIds.Chunk(500))
        {
            var rows = connection.Query<TagLinkRow>(
                "SELECT l.note_id AS NoteId, t.id AS Id, t.name AS Name, t.color AS Color FROM note_tag_links AS l JOIN note_tags AS t ON t.id = l.tag_id WHERE l.note_id IN @Ids ORDER BY t.name_folded, t.id",
                new { Ids = chunk },
                transaction);
            foreach (var row in rows)
            {
                if (!result.TryGetValue(row.NoteId, out var list))
                {
                    list = [];
                    result[row.NoteId] = list;
                }

                list.Add(new Tag(row.Id, row.Name, row.Color));
            }
        }

        return result;
    }

    private sealed class NoteRow
    {
        public long Id { get; set; }

        public string Title { get; set; } = string.Empty;

        public string Body { get; set; } = string.Empty;

        public long IsPinned { get; set; }

        public long CreatedUtc { get; set; }

        public long ModifiedUtc { get; set; }

        public long? DeletedUtc { get; set; }

        public long? ProjectId { get; set; }

        public Note ToNote(IReadOnlyList<Tag> tags) => new()
        {
            Id = Id,
            Title = Title,
            Body = Body,
            IsPinned = IsPinned != 0,
            CreatedUtc = SqliteTime.Decode(CreatedUtc),
            ModifiedUtc = SqliteTime.Decode(ModifiedUtc),
            DeletedUtc = DeletedUtc is { } deleted ? SqliteTime.Decode(deleted) : null,
            Tags = tags,
            ProjectId = ProjectId,
        };
    }

    private sealed class TagLinkRow
    {
        public long NoteId { get; set; }

        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string? Color { get; set; }
    }
}

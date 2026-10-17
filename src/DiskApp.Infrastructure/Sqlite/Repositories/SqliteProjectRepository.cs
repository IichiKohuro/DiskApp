using Dapper;

using DiskApp.Core.Domain;
using DiskApp.Core.Repositories;
using DiskApp.Core.Search;

using Microsoft.Data.Sqlite;

namespace DiskApp.Infrastructure.Sqlite.Repositories;

/// <summary>
/// Проекты в SQLite (PLAN.md, этап 24, ADR-0017): номер уникален без учёта регистра (<see cref="SearchText.Fold"/>),
/// число заметок — по <c>notes.project_id</c> без корзины.
/// </summary>
public sealed class SqliteProjectRepository(SqliteDatabase database) : SqliteRepository(database), IProjectRepository
{
    private const string Select = """
        SELECT p.id AS Id, p.number AS Number, p.title AS Title, p.color AS Color, p.folder_path AS FolderPath,
               p.created_utc AS CreatedUtc, p.modified_utc AS ModifiedUtc,
               (SELECT COUNT(*) FROM notes AS n WHERE n.project_id = p.id AND n.deleted_utc IS NULL) AS NoteCount
        FROM projects AS p
        """;

    /// <inheritdoc />
    public Task<IReadOnlyList<Project>> GetAllAsync(CancellationToken cancellationToken = default)
        => ReadAsync<IReadOnlyList<Project>>(connection => [.. connection.Query<ProjectRow>(Select + " ORDER BY p.number_folded, p.id").Select(static row => row.ToProject())], cancellationToken);

    /// <inheritdoc />
    public Task<Project?> GetAsync(long id, CancellationToken cancellationToken = default)
        => ReadAsync(connection => Get(connection, null, id), cancellationToken);

    /// <inheritdoc />
    public Task<Project?> FindByNumberAsync(string number, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(number);
        return ReadAsync(
            connection => connection.QuerySingleOrDefault<ProjectRow>(Select + " WHERE p.number_folded = @Folded", new { Folded = Fold(number) })?.ToProject(),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<Project> AddAsync(Project project, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        return WriteAsync(connection =>
        {
            var now = SqliteTime.Encode(nowUtc);
            var id = connection.ExecuteScalar<long>(
                "INSERT INTO projects (number, number_folded, title, color, folder_path, created_utc, modified_utc) VALUES (@Number, @Folded, @Title, @Color, @Folder, @Now, @Now) RETURNING id",
                new { project.Number, Folded = Fold(project.Number), project.Title, project.Color, Folder = project.FolderPath, Now = now });
            return Get(connection, null, id)!;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Project?> UpdateAsync(Project project, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        return WriteAsync(connection =>
        {
            var changed = connection.Execute(
                "UPDATE projects SET number = @Number, number_folded = @Folded, title = @Title, color = @Color, folder_path = @Folder, modified_utc = @Now WHERE id = @Id",
                new { project.Id, project.Number, Folded = Fold(project.Number), project.Title, project.Color, Folder = project.FolderPath, Now = SqliteTime.Encode(nowUtc) });
            return changed == 0 ? null : Get(connection, null, project.Id);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default)
        => WriteAsync(connection => connection.Execute("DELETE FROM projects WHERE id = @Id", new { Id = id }) == 1, cancellationToken);

    /// <inheritdoc />
    public Task<int> MoveFolderAsync(string oldPath, string newPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(oldPath);
        ArgumentNullException.ThrowIfNull(newPath);
        return WriteAsync(connection => connection.Execute("UPDATE projects SET folder_path = @To WHERE folder_path = @From", new { From = oldPath, To = newPath }), cancellationToken);
    }

    private static string Fold(string number) => SearchText.Fold(number.Trim());

    private static Project? Get(SqliteConnection connection, SqliteTransaction? transaction, long id)
        => connection.QuerySingleOrDefault<ProjectRow>(Select + " WHERE p.id = @Id", new { Id = id }, transaction)?.ToProject();

    private sealed class ProjectRow
    {
        public long Id { get; set; }

        public string Number { get; set; } = string.Empty;

        public string Title { get; set; } = string.Empty;

        public string? Color { get; set; }

        public string? FolderPath { get; set; }

        public long CreatedUtc { get; set; }

        public long ModifiedUtc { get; set; }

        public long NoteCount { get; set; }

        public Project ToProject() => new()
        {
            Id = Id,
            Number = Number,
            Title = Title,
            Color = Color,
            FolderPath = FolderPath,
            NoteCount = (int)NoteCount,
            CreatedUtc = SqliteTime.Decode(CreatedUtc),
            ModifiedUtc = SqliteTime.Decode(ModifiedUtc),
        };
    }
}

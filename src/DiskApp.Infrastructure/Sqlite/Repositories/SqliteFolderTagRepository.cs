using Dapper;

using DiskApp.Core.Domain;
using DiskApp.Core.Paths;
using DiskApp.Core.Repositories;

namespace DiskApp.Infrastructure.Sqlite.Repositories;

/// <summary>
/// Авто-теги папок в SQLite (PLAN.md, этап 19): таблица <c>folder_tags</c> — полный путь папки и тег. Пути сравниваются
/// по правилам ОС (в Windows без учёта регистра, в том числе кириллицы), поэтому совпадение ищется в .NET, а не через
/// <c>COLLATE NOCASE</c>: таких строк десятки.
/// </summary>
public sealed class SqliteFolderTagRepository(SqliteDatabase database, PathNormalizer paths) : SqliteRepository(database), IFolderTagRepository
{
    /// <inheritdoc />
    public Task<IReadOnlyList<FolderTags>> GetAllAsync(CancellationToken cancellationToken = default)
        => ReadAsync<IReadOnlyList<FolderTags>>(connection =>
        {
            var rows = connection.Query<FolderTagRow>(
                "SELECT ft.path AS Path, t.id AS Id, t.name AS Name, t.color AS Color FROM folder_tags AS ft JOIN tags AS t ON t.id = ft.tag_id ORDER BY ft.path, t.name_folded, t.id");
            return rows
                .GroupBy(static row => row.Path, StringComparer.Ordinal)
                .Select(static group => new FolderTags(group.Key, [.. group.Select(static row => new Tag(row.Id, row.Name, row.Color))]))
                .ToArray();
        }, cancellationToken);

    /// <inheritdoc />
    public Task SetAsync(string path, IReadOnlyList<long> tagIds, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(tagIds);

        return WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();

            // Прежняя запись папки могла быть сохранена в другом регистре: заменяется и она.
            foreach (var same in connection.Query<string>("SELECT DISTINCT path FROM folder_tags", transaction: transaction).Where(stored => paths.Comparer.Equals(stored, path)).ToArray())
            {
                connection.Execute("DELETE FROM folder_tags WHERE path = @Path", new { Path = same }, transaction);
            }

            foreach (var tagId in tagIds.Distinct())
            {
                connection.Execute("INSERT OR IGNORE INTO folder_tags (path, tag_id) VALUES (@Path, @TagId)", new { Path = path, TagId = tagId }, transaction);
            }

            transaction.Commit();
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task MovePathsAsync(IReadOnlyList<(string From, string To)> changes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            return Task.CompletedTask;
        }

        return WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            foreach (var (from, to) in changes)
            {
                // Если у нового пути уже есть тот же тег, строка остаётся одна.
                connection.Execute("UPDATE OR IGNORE folder_tags SET path = @To WHERE path = @From", new { From = from, To = to }, transaction);
                connection.Execute("DELETE FROM folder_tags WHERE path = @From", new { From = from }, transaction);
            }

            transaction.Commit();
        }, cancellationToken);
    }

    private sealed class FolderTagRow
    {
        public string Path { get; set; } = string.Empty;

        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string? Color { get; set; }
    }
}

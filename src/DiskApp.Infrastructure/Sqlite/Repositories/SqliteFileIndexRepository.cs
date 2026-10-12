using Dapper;

using DiskApp.Core.Domain;
using DiskApp.Core.Paths;
using DiskApp.Core.Repositories;
using DiskApp.Core.Search;

using Microsoft.Data.Sqlite;

namespace DiskApp.Infrastructure.Sqlite.Repositories;

/// <summary>
/// Записи индекса в SQLite. Пакетная запись — три подготовленных запроса на строку в одной транзакции:
/// пометить неизменённую, вставить новую, обновить изменённую. Поддеревья удаляются и переименовываются
/// диапазоном по <c>rel_path</c> (индекс <c>UNIQUE(root_id, rel_path)</c>), без построчной обработки.
/// </summary>
public sealed class SqliteFileIndexRepository : SqliteRepository, IFileIndexRepository
{
    // Без изменений — только при живой связи с родителем. Потерять её запись может одним путём: её вставили раньше
    // родителя (наблюдатель во время пересборки индекса), и parent_id остался NULL; такую запись чинит UpdateSql.
    // Неверного ненулевого родителя не бывает: удаление папки каскадом удаляет детей.
    private const string MarkUnchangedSql = """
        UPDATE entries SET scan_id = $scan
        WHERE root_id = $root AND rel_path = $rel_path AND is_dir = $is_dir AND name = $name
          AND size = $size AND modified_utc = $modified AND attributes = $attributes
          AND (parent_id IS NOT NULL OR $parent_rel_path IS NULL)
        """;

    private const string InsertSql = """
        INSERT INTO entries (root_id, parent_id, name, name_folded, ext, is_dir, size, created_utc, modified_utc, attributes, rel_path, kind, scan_id)
        VALUES ($root, (SELECT id FROM entries WHERE root_id = $root AND rel_path = $parent_rel_path), $name, $name_folded, $ext, $is_dir, $size,
                $created, $modified, $attributes, $rel_path, $kind, $scan)
        ON CONFLICT (root_id, rel_path) DO NOTHING
        """;

    private const string UpdateSql = """
        UPDATE entries SET
            parent_id = (SELECT p.id FROM entries AS p WHERE p.root_id = $root AND p.rel_path = $parent_rel_path),
            name = $name, name_folded = $name_folded, ext = $ext, is_dir = $is_dir, size = $size,
            created_utc = $created, modified_utc = $modified, attributes = $attributes, kind = $kind, scan_id = $scan
        WHERE root_id = $root AND rel_path = $rel_path
        """;

    private static readonly string[] UpsertParameters =
    [
        "$root", "$parent_rel_path", "$name", "$name_folded", "$ext", "$is_dir", "$size",
        "$created", "$modified", "$attributes", "$rel_path", "$kind", "$scan",
    ];

    private readonly PathNormalizer _paths;
    private readonly FileKindMap _kinds;

    /// <summary>
    /// Создаёт репозиторий.
    /// </summary>
    /// <param name="database">База.</param>
    /// <param name="paths">Правила путей для разделителя относительных путей; по умолчанию текущей ОС.</param>
    /// <param name="kinds">Таблица видов для переименованных файлов; по умолчанию встроенная.</param>
    public SqliteFileIndexRepository(SqliteDatabase database, PathNormalizer? paths = null, FileKindMap? kinds = null)
        : base(database)
    {
        _paths = paths ?? PathNormalizer.Current;
        _kinds = kinds ?? FileKindMap.Default;
    }

    /// <inheritdoc />
    public async Task<UpsertResult> UpsertAsync(IReadOnlyList<FileEntry> entries, long scanId, CancellationToken cancellationToken = default)
        => (await UpsertCoreAsync(entries, scanId, collectIndices: false, cancellationToken).ConfigureAwait(false)).Totals;

    /// <inheritdoc />
    public Task<UpsertDetails> UpsertDetailedAsync(IReadOnlyList<FileEntry> entries, long scanId, CancellationToken cancellationToken = default)
        => UpsertCoreAsync(entries, scanId, collectIndices: true, cancellationToken);

    /// <inheritdoc />
    public Task<long> NextScanIdAsync(long rootId, CancellationToken cancellationToken = default)
        => WriteAsync(connection => connection.ExecuteScalar<long>(
            "SELECT COALESCE(MAX(scan_id), 0) + 1 FROM entries WHERE root_id = @RootId",
            new { RootId = rootId }), cancellationToken);

    /// <inheritdoc />
    public Task<int> MarkSubtreeAsync(long rootId, string relPath, long scanId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relPath);
        var path = _paths.NormalizeRelative(relPath);
        var (from, to) = DescendantRange(path);

        return WriteAsync(connection => path.Length == 0
            ? connection.Execute("UPDATE entries SET scan_id = @ScanId WHERE root_id = @RootId", new { RootId = rootId, ScanId = scanId })
            : connection.Execute(
                "UPDATE entries SET scan_id = @ScanId WHERE root_id = @RootId AND (rel_path = @Path OR (rel_path >= @From AND rel_path < @To))",
                new { RootId = rootId, ScanId = scanId, Path = path, From = from, To = to }), cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> SweepSubtreeAsync(long rootId, string relPath, long scanId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relPath);
        var path = _paths.NormalizeRelative(relPath);
        if (path.Length == 0)
        {
            return SweepAsync(rootId, scanId, cancellationToken);
        }

        var (from, to) = DescendantRange(path);
        return WriteAsync(connection => DeleteCounted(
            connection,
            "root_id = @RootId AND rel_path >= @From AND rel_path < @To AND scan_id <> @ScanId",
            new { RootId = rootId, ScanId = scanId, From = from, To = to }), cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FileEntry>> GetChildrenOfPathAsync(long rootId, string relPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relPath);
        var path = _paths.NormalizeRelative(relPath);
        var sql = path.Length == 0
            ? $"SELECT {EntryRow.Columns} FROM entries WHERE root_id = @RootId AND parent_id IS NULL"
            : $"SELECT {EntryRow.Columns} FROM entries WHERE root_id = @RootId AND parent_id = (SELECT p.id FROM entries AS p WHERE p.root_id = @RootId AND p.rel_path = @Path)";

        return ReadAsync<IReadOnlyList<FileEntry>>(connection => connection
            .Query<EntryRow>(sql, new { RootId = rootId, Path = path })
            .Select(static row => row.ToEntry())
            .ToArray(), cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> DeleteSubtreeAsync(long rootId, string relPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relPath);
        var path = _paths.NormalizeRelative(relPath);
        var (from, to) = DescendantRange(path);

        return WriteAsync(connection => DeleteCounted(
            connection,
            "root_id = @RootId AND (rel_path = @Path OR (rel_path >= @From AND rel_path < @To))",
            new { RootId = rootId, Path = path, From = from, To = to }), cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> RenameSubtreeAsync(long rootId, string oldRelPath, string newRelPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldRelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newRelPath);

        var oldPath = _paths.NormalizeRelative(oldRelPath);
        var newPath = _paths.NormalizeRelative(newRelPath);
        if (_paths.Comparer.Equals(oldPath, newPath) && string.Equals(oldPath, newPath, StringComparison.Ordinal))
        {
            return Task.FromResult(0);
        }

        var (from, to) = DescendantRange(oldPath);
        var newName = FileNameOf(newPath);
        var newParent = ParentOf(newPath);

        return WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();

            var isDirectory = connection.ExecuteScalar<long?>(
                "SELECT is_dir FROM entries WHERE root_id = @RootId AND rel_path = @Path",
                new { RootId = rootId, Path = oldPath }, transaction);
            if (isDirectory is null)
            {
                return 0;
            }

            var extension = isDirectory != 0 ? string.Empty : FileEntry.ExtensionOf(newName);
            var affected = connection.Execute(
                """
                UPDATE entries SET
                    rel_path = @NewPath, name = @Name, name_folded = @NameFolded, ext = @Ext, kind = @Kind,
                    parent_id = (SELECT p.id FROM entries AS p WHERE p.root_id = @RootId AND p.rel_path = @NewParent)
                WHERE root_id = @RootId AND rel_path = @OldPath
                """,
                new
                {
                    RootId = rootId,
                    OldPath = oldPath,
                    NewPath = newPath,
                    Name = newName,
                    NameFolded = Fold(newName),
                    Ext = extension,
                    Kind = (long)_kinds.Classify(extension, isDirectory != 0),
                    NewParent = newParent,
                },
                transaction);

            if (isDirectory != 0)
            {
                affected += connection.Execute(
                    """
                    UPDATE entries SET rel_path = @NewPath || substr(rel_path, length(@OldPath) + 1)
                    WHERE root_id = @RootId AND rel_path >= @From AND rel_path < @To
                    """,
                    new { RootId = rootId, OldPath = oldPath, NewPath = newPath, From = from, To = to },
                    transaction);
            }

            transaction.Commit();
            return affected;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> SweepAsync(long rootId, long scanId, CancellationToken cancellationToken = default)
        => WriteAsync(connection => DeleteCounted(
            connection,
            "root_id = @RootId AND scan_id <> @ScanId",
            new { RootId = rootId, ScanId = scanId }), cancellationToken);

    /// <inheritdoc />
    public Task<int> DeleteByRootAsync(long rootId, CancellationToken cancellationToken = default)
        => WriteAsync(connection => DeleteCounted(connection, "root_id = @RootId", new { RootId = rootId }), cancellationToken);

    /// <inheritdoc />
    public Task<FileEntry?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
        => ReadAsync(connection => connection
            .QuerySingleOrDefault<EntryRow>($"SELECT {EntryRow.Columns} FROM entries WHERE id = @Id", new { Id = id })?
            .ToEntry(), cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<FileEntry>> GetManyAsync(IReadOnlyList<long> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<FileEntry>>([]);
        }

        return ReadAsync<IReadOnlyList<FileEntry>>(connection =>
        {
            var result = new List<FileEntry>(ids.Count);
            foreach (var chunk in ids.Chunk(500))
            {
                result.AddRange(connection
                    .Query<EntryRow>($"SELECT {EntryRow.Columns} FROM entries WHERE id IN @Ids", new { Ids = chunk })
                    .Select(static row => row.ToEntry()));
            }

            return result;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<FileEntry?> GetByPathAsync(long rootId, string relPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(relPath);
        var path = _paths.NormalizeRelative(relPath);

        return ReadAsync(connection => connection
            .QuerySingleOrDefault<EntryRow>(
                $"SELECT {EntryRow.Columns} FROM entries WHERE root_id = @RootId AND rel_path = @Path",
                new { RootId = rootId, Path = path })?
            .ToEntry(), cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FileEntry>> GetChildrenAsync(
        long rootId,
        long? parentId,
        SearchSortField sortField,
        bool descending,
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var direction = descending ? "DESC" : "ASC";
        var order = sortField switch
        {
            SearchSortField.Modified => $"modified_utc {direction}",
            SearchSortField.Created => $"created_utc {direction}",
            SearchSortField.Size => $"size {direction}",
            _ => $"name_folded {direction}",
        };

        var sql = $"""
            SELECT {EntryRow.Columns} FROM entries
            WHERE root_id = @RootId AND parent_id IS @ParentId
            ORDER BY is_dir DESC, {order}, id
            LIMIT @Limit OFFSET @Offset
            """;

        return ReadAsync<IReadOnlyList<FileEntry>>(connection => connection
            .Query<EntryRow>(sql, new { RootId = rootId, ParentId = parentId, Limit = limit, Offset = offset })
            .Select(static row => row.ToEntry())
            .ToArray(), cancellationToken);
    }

    /// <inheritdoc />
    public Task<IndexStats> GetStatsAsync(long rootId, CancellationToken cancellationToken = default)
        => ReadAsync(connection =>
        {
            var row = connection.QuerySingle<(long Files, long Directories, long TotalSize)>(
                """
                SELECT COUNT(CASE WHEN is_dir = 0 THEN 1 END),
                       COUNT(CASE WHEN is_dir <> 0 THEN 1 END),
                       COALESCE(SUM(CASE WHEN is_dir = 0 THEN size END), 0)
                FROM entries WHERE root_id = @RootId
                """,
                new { RootId = rootId });
            return new IndexStats(rootId, row.Files, row.Directories, row.TotalSize);
        }, cancellationToken);

    /// <inheritdoc />
    public Task<long> CountAsync(CancellationToken cancellationToken = default)
        => ReadAsync(connection => connection.ExecuteScalar<long>("SELECT COUNT(*) FROM entries"), cancellationToken);

    /// <inheritdoc />
    public Task<PathCount> CountPathsAsync(Func<string, bool> matches, TimeSpan timeLimit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(matches);

        return ReadAsync(connection =>
        {
            var deadline = Environment.TickCount64 + (long)timeLimit.TotalMilliseconds;
            long matched = 0;
            long checkedCount = 0;
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT rel_path FROM entries";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (matches(reader.GetString(0)))
                {
                    matched++;
                }

                // Проверка времени и отмены — раз в 4 096 записей, чтобы не замедлять чтение.
                if ((++checkedCount & 0xFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Environment.TickCount64 > deadline)
                    {
                        return new PathCount(matched, checkedCount, Complete: false);
                    }
                }
            }

            return new PathCount(matched, checkedCount, Complete: true);
        }, cancellationToken);
    }

    /// <summary>
    /// Имя для колонки <c>name_folded</c>: свёртка для поиска (<see cref="SearchText.Fold"/>, ADR-0005).
    /// </summary>
    internal static string Fold(string name) => SearchText.Fold(name);

    private Task<UpsertDetails> UpsertCoreAsync(IReadOnlyList<FileEntry> entries, long scanId, bool collectIndices, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return Task.FromResult(UpsertDetails.Empty);
        }

        return WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var mark = Prepare(connection, transaction, MarkUnchangedSql);
            using var insert = Prepare(connection, transaction, InsertSql);
            using var update = Prepare(connection, transaction, UpdateSql);
            SqliteCommand[] commands = [mark, insert, update];

            var unchanged = 0;
            var insertedIndices = new List<int>(collectIndices ? 16 : 0);
            var updatedIndices = new List<int>(collectIndices ? 16 : 0);
            var inserted = 0;
            var updated = 0;

            for (var i = 0; i < entries.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Bind(commands, entries[i], scanId);

                if (mark.ExecuteNonQuery() == 1)
                {
                    unchanged++;
                }
                else if (insert.ExecuteNonQuery() == 1)
                {
                    inserted++;
                    if (collectIndices)
                    {
                        insertedIndices.Add(i);
                    }
                }
                else
                {
                    update.ExecuteNonQuery();
                    updated++;
                    if (collectIndices)
                    {
                        updatedIndices.Add(i);
                    }
                }
            }

            transaction.Commit();
            return new UpsertDetails(new UpsertResult(inserted, updated, unchanged), insertedIndices, updatedIndices);
        }, cancellationToken);
    }

    /// <summary>
    /// Удаляет записи по условию и возвращает их число. Считается отдельным запросом: каскад по <c>parent_id</c>
    /// удаляет детей раньше, чем до них доходит сам <c>DELETE</c>, и <c>changes()</c> их не учитывает.
    /// </summary>
    private static int DeleteCounted(SqliteConnection connection, string where, object parameters)
    {
        using var transaction = connection.BeginTransaction();
        var count = connection.ExecuteScalar<int>($"SELECT COUNT(*) FROM entries WHERE {where}", parameters, transaction);
        if (count > 0)
        {
            connection.Execute($"DELETE FROM entries WHERE {where}", parameters, transaction);
        }

        transaction.Commit();
        return count;
    }

    private static SqliteCommand Prepare(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var name in UpsertParameters)
        {
            // Тип параметра выводится из значения: числа и даты-тики уходят в SQLite целыми, а не текстом.
            command.Parameters.Add(new SqliteParameter { ParameterName = name });
        }

        command.Prepare();
        return command;
    }

    private void Bind(SqliteCommand[] commands, FileEntry entry, long scanId)
    {
        var relPath = _paths.NormalizeRelative(entry.RelPath);
        object parentRelPath = ParentOf(relPath) is { } parent ? parent : DBNull.Value;
        var created = SqliteTime.Encode(entry.CreatedUtc);
        var modified = SqliteTime.Encode(entry.ModifiedUtc);
        var nameFolded = Fold(entry.Name);

        foreach (var command in commands)
        {
            var parameters = command.Parameters;
            parameters["$root"].Value = entry.RootId;
            parameters["$parent_rel_path"].Value = parentRelPath;
            parameters["$name"].Value = entry.Name;
            parameters["$name_folded"].Value = nameFolded;
            parameters["$ext"].Value = entry.Extension;
            parameters["$is_dir"].Value = entry.IsDirectory ? 1L : 0L;
            parameters["$size"].Value = entry.Size;
            parameters["$created"].Value = created;
            parameters["$modified"].Value = modified;
            parameters["$attributes"].Value = (long)entry.Attributes;
            parameters["$rel_path"].Value = relPath;
            parameters["$kind"].Value = (long)entry.Kind;
            parameters["$scan"].Value = scanId;
        }
    }

    private (string From, string To) DescendantRange(string relPath)
    {
        var separator = _paths.Separator;
        return (relPath + separator, relPath + (char)(separator + 1));
    }

    private string? ParentOf(string relPath)
    {
        var index = relPath.LastIndexOf(_paths.Separator);
        return index < 0 ? null : relPath[..index];
    }

    private string FileNameOf(string relPath)
    {
        var index = relPath.LastIndexOf(_paths.Separator);
        return index < 0 ? relPath : relPath[(index + 1)..];
    }
}

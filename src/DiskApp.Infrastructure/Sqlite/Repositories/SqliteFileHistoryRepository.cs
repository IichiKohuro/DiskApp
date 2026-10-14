using System.Globalization;

using Dapper;

using DiskApp.Core.History;
using DiskApp.Core.Paths;
using DiskApp.Core.Search;

namespace DiskApp.Infrastructure.Sqlite.Repositories;

/// <summary>
/// История файлов в SQLite (PLAN.md, этап 20): таблица <c>file_events</c>. Пути для сравнения — ключи в нижнем регистре там,
/// где регистр путей не важен; поиск по имени — подстрока свёрнутых имени и прежнего имени.
/// </summary>
public sealed class SqliteFileHistoryRepository(SqliteDatabase database, PathNormalizer paths) : SqliteRepository(database), IFileHistoryRepository
{
    private const string Columns = "id AS Id, time_utc AS Time, kind AS Kind, source AS Source, path AS Path, old_path AS OldPath, is_dir AS IsDirectory, size AS Size, operation_id AS OperationId, details AS Details";

    /// <inheritdoc />
    public Task<IReadOnlyList<FileEvent>> AddAsync(IReadOnlyList<FileEvent> events, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<FileEvent>>([]);
        }

        return WriteAsync<IReadOnlyList<FileEvent>>(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var saved = new List<FileEvent>(events.Count);
            foreach (var item in events)
            {
                var id = connection.ExecuteScalar<long>(
                    """
                    INSERT INTO file_events (time_utc, kind, source, path, old_path, path_key, old_path_key, names_folded, is_dir, size, operation_id, details)
                    VALUES (@Time, @Kind, @Source, @Path, @OldPath, @PathKey, @OldPathKey, @Names, @IsDirectory, @Size, @OperationId, @Details)
                    RETURNING id
                    """,
                    new
                    {
                        Time = SqliteTime.Encode(item.TimeUtc),
                        Kind = (int)item.Kind,
                        Source = (int)item.Source,
                        item.Path,
                        item.OldPath,
                        PathKey = Key(item.Path),
                        OldPathKey = item.OldPath is null ? null : Key(item.OldPath),
                        Names = SearchText.Fold(item.Name) + "\n" + (item.OldName is null ? string.Empty : SearchText.Fold(item.OldName)),
                        IsDirectory = item.IsDirectory ? 1 : 0,
                        item.Size,
                        OperationId = item.OperationId?.ToString("D"),
                        item.Details,
                    },
                    transaction);
                saved.Add(item with { Id = id });
            }

            transaction.Commit();
            return saved;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FileEvent>> QueryAsync(FileHistoryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var clauses = new List<string>();
        var parameters = new DynamicParameters();
        if (query.Folder is { Length: > 0 } folder)
        {
            var key = Key(folder.TrimEnd(paths.Separator));
            parameters.Add("Folder", key);
            parameters.Add("From", key + paths.Separator);
            parameters.Add("To", key + (char)(paths.Separator + 1));
            clauses.Add("(path_key = @Folder OR (path_key >= @From AND path_key < @To) OR (old_path_key >= @From AND old_path_key < @To))");
        }

        if (query.Text is { Length: > 0 } text && SearchText.Fold(text.Trim()) is { Length: > 0 } folded)
        {
            parameters.Add("Text", folded);
            clauses.Add("instr(names_folded, @Text) > 0");
        }

        if (query.Kinds.Count > 0)
        {
            clauses.Add("kind IN (" + string.Join(", ", query.Kinds.Select(static kind => ((int)kind).ToString(CultureInfo.InvariantCulture))) + ")");
        }

        if (query.Sources.Count > 0)
        {
            clauses.Add("source IN (" + string.Join(", ", query.Sources.Select(static source => ((int)source).ToString(CultureInfo.InvariantCulture))) + ")");
        }

        if (query.FromUtc is { } from)
        {
            parameters.Add("FromUtc", SqliteTime.Encode(from));
            clauses.Add("time_utc >= @FromUtc");
        }

        if (query.ToUtc is { } to)
        {
            parameters.Add("ToUtc", SqliteTime.Encode(to));
            clauses.Add("time_utc < @ToUtc");
        }

        if (query.BeforeId is { } before)
        {
            parameters.Add("Before", before);
            clauses.Add("id < @Before");
        }

        parameters.Add("Limit", Math.Clamp(query.Limit, 1, FileHistoryQuery.MaxLimit));
        var where = clauses.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", clauses);
        return ReadAsync<IReadOnlyList<FileEvent>>(
            connection => [.. connection.Query<EventRow>($"SELECT {Columns} FROM file_events {where} ORDER BY id DESC LIMIT @Limit", parameters).Select(ToEvent)],
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<FileEvent?> GetAsync(long id, CancellationToken cancellationToken = default)
        => ReadAsync(
            connection => connection.QuerySingleOrDefault<EventRow>($"SELECT {Columns} FROM file_events WHERE id = @Id", new { Id = id }) is { } row ? ToEvent(row) : null,
            cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<FileEvent>> GetByPathAsync(string path, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return ReadAsync<IReadOnlyList<FileEvent>>(
            connection => [.. connection.Query<EventRow>(
                $"SELECT {Columns} FROM file_events WHERE path_key = @Key UNION SELECT {Columns} FROM file_events WHERE old_path_key = @Key ORDER BY Id DESC LIMIT @Limit",
                new { Key = Key(path), Limit = limit }).Select(ToEvent)],
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> DeleteOlderThanAsync(DateTimeOffset utc, CancellationToken cancellationToken = default)
        => WriteAsync(connection => connection.Execute("DELETE FROM file_events WHERE time_utc < @Time", new { Time = SqliteTime.Encode(utc) }), cancellationToken);

    /// <inheritdoc />
    public Task<long> EstimateSizeAsync(CancellationToken cancellationToken = default)
        => ReadAsync(
            // Пути хранятся дважды (как есть и ключом) и попадают в индекс ключей — отсюда тройной вес; 64 байта — числа и служебное.
            connection => connection.ExecuteScalar<long>(
                """
                SELECT COALESCE(SUM(64 + 3 * length(CAST(path AS BLOB)) + 3 * COALESCE(length(CAST(old_path AS BLOB)), 0)
                    + length(CAST(names_folded AS BLOB)) + COALESCE(length(CAST(details AS BLOB)), 0) + COALESCE(length(operation_id), 0)), 0)
                FROM file_events
                """),
            cancellationToken);

    /// <inheritdoc />
    public Task<int> DeleteOldestAsync(int count, CancellationToken cancellationToken = default)
        => WriteAsync(connection => connection.Execute("DELETE FROM file_events WHERE id IN (SELECT id FROM file_events ORDER BY id LIMIT @Count)", new { Count = count }), cancellationToken);

    private string Key(string path) => paths.Comparison == StringComparison.OrdinalIgnoreCase ? path.ToLowerInvariant() : path;

    private static FileEvent ToEvent(EventRow row)
        => new(
            row.Id,
            SqliteTime.Decode(row.Time),
            (FileEventKind)row.Kind,
            (FileEventSource)row.Source,
            row.Path,
            row.OldPath,
            row.IsDirectory != 0,
            row.Size,
            row.OperationId is { } operation && Guid.TryParse(operation, out var id) ? id : null,
            row.Details);

    private sealed class EventRow
    {
        public long Id { get; set; }

        public long Time { get; set; }

        public long Kind { get; set; }

        public long Source { get; set; }

        public string Path { get; set; } = string.Empty;

        public string? OldPath { get; set; }

        public long IsDirectory { get; set; }

        public long Size { get; set; }

        public string? OperationId { get; set; }

        public string? Details { get; set; }
    }
}

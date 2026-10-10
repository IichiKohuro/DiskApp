using Dapper;

using DiskApp.Core.Abstractions;
using DiskApp.Core.Domain;
using DiskApp.Core.Repositories;

namespace DiskApp.Infrastructure.Sqlite.Repositories;

/// <summary>
/// Корни индекса в SQLite. Удаление корня каскадно удаляет записи, статистику, избранное и теги его файлов.
/// </summary>
public sealed class SqliteRootRepository(SqliteDatabase database, IClock clock) : SqliteRepository(database), IRootRepository
{
    private const string Columns = "id, path, label, is_enabled, include_hidden, include_system, max_depth, last_scan_utc, status, error_message";

    /// <inheritdoc />
    public Task<IReadOnlyList<Root>> GetAllAsync(CancellationToken cancellationToken = default)
        => ReadAsync<IReadOnlyList<Root>>(connection => connection
            .Query<RootRow>($"SELECT {Columns} FROM roots ORDER BY id")
            .Select(static row => row.ToRoot())
            .ToArray(), cancellationToken);

    /// <inheritdoc />
    public Task<Root?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
        => ReadAsync(connection => connection
            .QuerySingleOrDefault<RootRow>($"SELECT {Columns} FROM roots WHERE id = @Id", new { Id = id })?
            .ToRoot(), cancellationToken);

    /// <inheritdoc />
    public Task<Root> AddAsync(Root root, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        var createdUtc = SqliteTime.Encode(clock.UtcNow);

        return WriteAsync(connection =>
        {
            var id = connection.ExecuteScalar<long>(
                """
                INSERT INTO roots (path, label, is_enabled, include_hidden, include_system, max_depth, last_scan_utc, status, error_message, created_utc)
                VALUES (@Path, @Label, @IsEnabled, @IncludeHidden, @IncludeSystem, @MaxDepth, @LastScanUtc, @Status, @ErrorMessage, @CreatedUtc);
                SELECT last_insert_rowid();
                """,
                new
                {
                    root.Path,
                    root.Label,
                    IsEnabled = root.IsEnabled ? 1 : 0,
                    IncludeHidden = root.IncludeHidden ? 1 : 0,
                    IncludeSystem = root.IncludeSystem ? 1 : 0,
                    root.MaxDepth,
                    LastScanUtc = SqliteTime.Encode(root.LastScanUtc),
                    Status = (int)root.Status,
                    root.ErrorMessage,
                    CreatedUtc = createdUtc,
                });

            return root with { Id = id };
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> UpdateAsync(Root root, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);

        return WriteAsync(connection => connection.Execute(
            """
            UPDATE roots SET path = @Path, label = @Label, is_enabled = @IsEnabled, include_hidden = @IncludeHidden,
                include_system = @IncludeSystem, max_depth = @MaxDepth, last_scan_utc = @LastScanUtc, status = @Status, error_message = @ErrorMessage
            WHERE id = @Id
            """,
            new
            {
                root.Id,
                root.Path,
                root.Label,
                IsEnabled = root.IsEnabled ? 1 : 0,
                IncludeHidden = root.IncludeHidden ? 1 : 0,
                IncludeSystem = root.IncludeSystem ? 1 : 0,
                root.MaxDepth,
                LastScanUtc = SqliteTime.Encode(root.LastScanUtc),
                Status = (int)root.Status,
                root.ErrorMessage,
            }) == 1, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default)
        => WriteAsync(connection => connection.Execute("DELETE FROM roots WHERE id = @Id", new { Id = id }) == 1, cancellationToken);

    /// <inheritdoc />
    public Task SetStatusAsync(long id, RootStatus status, string? errorMessage = null, CancellationToken cancellationToken = default)
        => WriteAsync(connection => connection.Execute(
            "UPDATE roots SET status = @Status, error_message = @ErrorMessage WHERE id = @Id",
            new { Id = id, Status = (int)status, ErrorMessage = errorMessage }), cancellationToken);

    /// <inheritdoc />
    public Task SetLastScanAsync(long id, DateTimeOffset scannedAtUtc, CancellationToken cancellationToken = default)
        => WriteAsync(connection => connection.Execute(
            "UPDATE roots SET last_scan_utc = @LastScanUtc WHERE id = @Id",
            new { Id = id, LastScanUtc = SqliteTime.Encode(scannedAtUtc) }), cancellationToken);

    private sealed class RootRow
    {
        public long Id { get; set; }

        public string Path { get; set; } = string.Empty;

        public string Label { get; set; } = string.Empty;

        public long IsEnabled { get; set; }

        public long IncludeHidden { get; set; }

        public long IncludeSystem { get; set; }

        public long? MaxDepth { get; set; }

        public long? LastScanUtc { get; set; }

        public long Status { get; set; }

        public string? ErrorMessage { get; set; }

        public Root ToRoot() => new()
        {
            Id = Id,
            Path = Path,
            Label = Label,
            IsEnabled = IsEnabled != 0,
            IncludeHidden = IncludeHidden != 0,
            IncludeSystem = IncludeSystem != 0,
            MaxDepth = MaxDepth is null ? null : checked((int)MaxDepth.Value),
            LastScanUtc = SqliteTime.DecodeOrNull(LastScanUtc),
            Status = (RootStatus)Status,
            ErrorMessage = ErrorMessage,
        };
    }
}

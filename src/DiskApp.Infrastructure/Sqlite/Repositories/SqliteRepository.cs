using Microsoft.Data.Sqlite;

namespace DiskApp.Infrastructure.Sqlite.Repositories;

/// <summary>
/// Общее для репозиториев SQLite: чтение на пуле потоков через соединение из пула,
/// запись через единственного писателя <see cref="SqliteDatabase.Writer"/>.
/// Microsoft.Data.Sqlite синхронна внутри, поэтому «асинхронность» здесь — перенос с UI-потока.
/// </summary>
public abstract class SqliteRepository
{
    /// <summary>
    /// Запоминает базу.
    /// </summary>
    protected SqliteRepository(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        Database = database;
    }

    /// <summary>База с фабрикой соединений и писателем.</summary>
    protected SqliteDatabase Database { get; }

    /// <summary>
    /// Выполняет запрос на чтение в отдельном соединении из пула.
    /// </summary>
    protected Task<T> ReadAsync<T>(Func<SqliteConnection, T> query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = Database.Connections.Open();
            return query(connection);
        }, cancellationToken);
    }

    /// <summary>
    /// Выполняет изменение через писателя и возвращает результат.
    /// </summary>
    protected Task<T> WriteAsync<T>(Func<SqliteConnection, T> work, CancellationToken cancellationToken)
        => Database.Writer.EnqueueAsync(work, cancellationToken);

    /// <summary>
    /// Выполняет изменение через писателя.
    /// </summary>
    protected Task WriteAsync(Action<SqliteConnection> work, CancellationToken cancellationToken)
        => Database.Writer.EnqueueAsync(work, cancellationToken);
}

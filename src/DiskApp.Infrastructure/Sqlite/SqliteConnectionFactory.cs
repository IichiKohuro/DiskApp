using Microsoft.Data.Sqlite;

namespace DiskApp.Infrastructure.Sqlite;

/// <summary>
/// Открывает соединения к файлу индекса с настройками из PLAN.md (этап 1.3): WAL, <c>synchronous=NORMAL</c>,
/// временные таблицы в памяти, кэш 64 МБ, <c>mmap</c> 256 МБ, внешние ключи, ожидание занятой базы 5 с.
/// Соединения берутся из пула Microsoft.Data.Sqlite; PRAGMA применяются при каждом открытии, это дёшево.
/// </summary>
public sealed class SqliteConnectionFactory
{
    private const string Pragmas = """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        PRAGMA temp_store = MEMORY;
        PRAGMA cache_size = -65536;
        PRAGMA mmap_size = 268435456;
        PRAGMA foreign_keys = ON;
        PRAGMA busy_timeout = 5000;
        """;

    /// <summary>
    /// Создаёт фабрику для файла базы; сам файл появится при первом открытии.
    /// </summary>
    /// <param name="databasePath">Абсолютный путь к <c>index.db</c>.</param>
    public SqliteConnectionFactory(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        SqliteGlobalConfiguration.Apply();
        DapperConfiguration.EnsureConfigured();
        DatabasePath = Path.GetFullPath(databasePath);
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 5,
        }.ToString();
    }

    /// <summary>Абсолютный путь к файлу базы.</summary>
    public string DatabasePath { get; }

    /// <summary>Строка подключения с созданием файла при отсутствии.</summary>
    public string ConnectionString { get; }

    /// <summary>
    /// Открывает соединение и применяет PRAGMA. Закрыть его (<c>using</c>) — обязанность вызывающего.
    /// </summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(ConnectionString);
        try
        {
            connection.Open();
            ApplyPragmas(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Асинхронная обёртка над <see cref="Open"/>: Microsoft.Data.Sqlite открывает файл синхронно,
    /// поэтому вызов переносится в пул потоков, чтобы не блокировать UI-поток.
    /// </summary>
    public Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
        => Task.Run(Open, cancellationToken);

    /// <summary>
    /// Сбрасывает пул соединений к этой базе, чтобы файл можно было переименовать или удалить.
    /// </summary>
    public void ClearPool()
    {
        using var connection = new SqliteConnection(ConnectionString);
        SqliteConnection.ClearPool(connection);
    }

    private static void ApplyPragmas(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        command.ExecuteNonQuery();
    }
}

using Microsoft.Data.Sqlite;

namespace DiskApp.Infrastructure.Sqlite;

/// <summary>
/// Открывает соединения к файлу индекса с настройками из PLAN.md (этап 1.3): WAL, <c>synchronous=NORMAL</c>,
/// временные таблицы в памяти, кэш 64 МБ, <c>mmap</c> 256 МБ, внешние ключи, ожидание занятой базы 5 с.
/// Соединения берутся из пула Microsoft.Data.Sqlite; PRAGMA применяются при каждом открытии, это дёшево.
/// </summary>
/// <remarks>
/// Выдача соединения из пула и очистка пулов во всём процессе идут под одной блокировкой. Microsoft.Data.Sqlite 10.0.12
/// (dotnet/efcore#39008, исправлено после этой версии) помечает выданное соединение активным раньше, чем запоминает
/// владельца. Если в этот миг пул очищается или к тому же файлу открывается другое соединение, библиотека принимает
/// выданное соединение за утёкшее: закрывает его под владельцем (<see cref="ObjectDisposedException"/> у писателя при
/// старте) или отдаёт второму владельцу, и два потока работают через одно нативное соединение. Под блокировкой —
/// только выдача из пула, микросекунды; PRAGMA выполняются вне её.
/// </remarks>
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

    private static readonly Lock PoolGate = new();

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

    private long _lastOpened = Environment.TickCount64;
    private long _openCount;

    /// <summary>Абсолютный путь к файлу базы.</summary>
    public string DatabasePath { get; }

    /// <summary>Сколько прошло с открытия последнего соединения (обслуживание при простое, этап 10.2).</summary>
    public TimeSpan SinceLastOpen => TimeSpan.FromMilliseconds(Environment.TickCount64 - Volatile.Read(ref _lastOpened));

    /// <summary>Сколько соединений открыто за всё время: по нему видно, было ли чтение с прошлого обслуживания.</summary>
    public long OpenCount => Interlocked.Read(ref _openCount);

    /// <summary>Строка подключения с созданием файла при отсутствии.</summary>
    public string ConnectionString { get; }

    /// <summary>
    /// Открывает соединение и применяет PRAGMA. Закрыть его (<c>using</c>) — обязанность вызывающего.
    /// </summary>
    public SqliteConnection Open()
    {
        Volatile.Write(ref _lastOpened, Environment.TickCount64);
        Interlocked.Increment(ref _openCount);
        var connection = OpenWithoutPragmas();
        try
        {
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
    /// Открывает соединение без PRAGMA и без учёта в простое: для проверки целостности файла, который может оказаться
    /// повреждённым.
    /// </summary>
    internal SqliteConnection OpenWithoutPragmas()
    {
        var connection = new SqliteConnection(ConnectionString);
        try
        {
            OpenPooled(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Открывает соединение с любой строкой подключения так, чтобы выдача из пула не совпала с очисткой пулов или
    /// другим открытием (см. замечания к классу). Нужно и тестам, которые открывают файл своей строкой подключения.
    /// </summary>
    internal static void OpenPooled(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        lock (PoolGate)
        {
            connection.Open();
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
        lock (PoolGate)
        {
            SqliteConnection.ClearPool(connection);
        }
    }

    /// <summary>
    /// Сбрасывает пулы соединений ко всем базам процесса, чтобы тесты и замеры могли удалить файлы, открытые любой
    /// строкой подключения. Используется вместо <see cref="SqliteConnection.ClearAllPools"/>: очистка не должна совпасть
    /// с выдачей соединения из пула в другом потоке (см. замечания к классу).
    /// </summary>
    public static void ClearAllPools()
    {
        lock (PoolGate)
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static void ApplyPragmas(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        command.ExecuteNonQuery();
    }
}

using System.Globalization;

using Microsoft.Data.Sqlite;

namespace DiskApp.Infrastructure.Sqlite;

/// <summary>
/// Файл индекса целиком: проверка целостности при старте, восстановление повреждённого файла, миграции,
/// единственный писатель и обслуживание (<c>PRAGMA optimize</c>, <c>VACUUM</c>). Репозитории получают
/// отсюда соединения для чтения и очередь для записи.
/// </summary>
public sealed class SqliteDatabase : IDisposable, IAsyncDisposable
{
    private const string IntegrityOk = "ok";
    private static readonly string[] SidecarSuffixes = ["-wal", "-shm", "-journal"];

    private static readonly TimeSpan IdleCheckInterval = TimeSpan.FromSeconds(15);

    private SqliteWriter? _writer;
    private Timer? _idleTimer;
    private long _releasedVersion = -1;
    private int _releasing;
    private bool _disposed;

    /// <summary>
    /// Простой без чтений и записей, после которого база отдаёт память (этап 10.2): соединения пула закрываются вместе с
    /// кэшем страниц (до 64 МБ каждое), писатель выполняет <c>PRAGMA optimize</c>, усекает WAL и сжимает свой кэш. Во время
    /// работы кэш нужен: на 1 млн записей запросы с фильтрами при 8 МБ кэша выходят из бюджета поиска.
    /// </summary>
    public TimeSpan IdleDelay { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Создаёт объект базы; файл открывается в <see cref="InitializeAsync"/>.
    /// </summary>
    /// <param name="databasePath">Абсолютный путь к <c>index.db</c>.</param>
    public SqliteDatabase(string databasePath)
    {
        Connections = new SqliteConnectionFactory(databasePath);
    }

    /// <summary>Фабрика соединений для чтения.</summary>
    public SqliteConnectionFactory Connections { get; }

    /// <summary>Путь к файлу базы.</summary>
    public string DatabasePath => Connections.DatabasePath;

    /// <summary>
    /// Очередь писателя; доступна после <see cref="InitializeAsync"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">База ещё не инициализирована.</exception>
    public SqliteWriter Writer => _writer ?? throw new InvalidOperationException("База не инициализирована: вызовите InitializeAsync.");

    /// <summary>
    /// Готовит базу к работе: создаёт папку, проверяет существующий файл (<c>PRAGMA quick_check</c>),
    /// повреждённый переименовывает в <c>index.corrupt-&lt;дата&gt;.db</c> и создаёт новый, применяет миграции, запускает писателя.
    /// </summary>
    public async Task<DatabaseInitialization> InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_writer is not null)
        {
            throw new InvalidOperationException("База уже инициализирована.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);

        string? corruptPath = null;
        var existed = File.Exists(DatabasePath);
        if (existed && !await Task.Run(CheckIntegrity, cancellationToken).ConfigureAwait(false))
        {
            corruptPath = QuarantineCorruptFile();
        }

        _writer = new SqliteWriter(Connections);
        _idleTimer = new Timer(static state => ((SqliteDatabase)state!).OnIdleTick(), this, IdleCheckInterval, IdleCheckInterval);
        var applied = await _writer.EnqueueAsync(static connection => SqliteMigrator.Apply(connection), cancellationToken).ConfigureAwait(false);

        return new DatabaseInitialization(
            DatabasePath,
            SqliteMigrator.LatestVersion,
            applied,
            WasCreated: !existed || corruptPath is not null,
            CorruptFilePath: corruptPath);
    }

    /// <summary>
    /// Быстрая проверка целостности: <c>true</c>, если <c>PRAGMA quick_check</c> вернул <c>ok</c>;
    /// <c>false</c>, если файл повреждён или это вообще не база SQLite.
    /// </summary>
    public bool CheckIntegrity()
    {
        try
        {
            using var connection = new SqliteConnection(Connections.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check";
            using var reader = command.ExecuteReader();
            return reader.Read() && string.Equals(reader.GetString(0), IntegrityOk, StringComparison.OrdinalIgnoreCase) && !reader.Read();
        }
        catch (SqliteException)
        {
            return false;
        }
        finally
        {
            Connections.ClearPool();
        }
    }

    /// <summary>
    /// <c>PRAGMA optimize</c> и усечение WAL; вызывать при простое (этап 10).
    /// </summary>
    public Task OptimizeAsync(CancellationToken cancellationToken = default)
        => Writer.EnqueueAsync(static connection => Execute(connection, "PRAGMA optimize; PRAGMA wal_checkpoint(TRUNCATE);"), cancellationToken);

    /// <summary>
    /// Отдаёт память, если база простаивает дольше <see cref="IdleDelay"/> и с прошлого раза были чтения или записи; за один
    /// простой — один раз. Завершается, когда писатель сжал свой кэш.
    /// </summary>
    /// <returns><c>true</c>, если память отдана сейчас.</returns>
    public async Task<bool> ReleaseIdleMemoryAsync()
    {
        if (_disposed || _writer is not { IsDisposed: false } writer)
        {
            return false;
        }

        if (Connections.SinceLastOpen < IdleDelay || writer.SinceLastActivity < IdleDelay || ActivityVersion == Interlocked.Read(ref _releasedVersion))
        {
            return false;
        }

        if (Interlocked.Exchange(ref _releasing, 1) == 1)
        {
            return false;
        }

        try
        {
            Connections.ClearPool();
            await writer.EnqueueAsync(static connection => Execute(connection, "PRAGMA optimize; PRAGMA wal_checkpoint(TRUNCATE); PRAGMA shrink_memory;"))
                .ConfigureAwait(false);

            // Собственное задание обслуживания тоже считается записью: версия берётся после него.
            Interlocked.Exchange(ref _releasedVersion, ActivityVersion);
            return true;
        }
        finally
        {
            Volatile.Write(ref _releasing, 0);
        }
    }

    // Растёт при каждом открытом соединении и каждой записи: так видно, была ли работа с прошлого обслуживания.
    private long ActivityVersion => Connections.OpenCount + (_writer?.CompletedCount ?? 0);

    private void OnIdleTick()
        => _ = ReleaseIdleMemoryAsync().ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);

    /// <summary>
    /// <c>VACUUM</c>: пересобирает файл после больших удалений; требует места на диске и выполняется вне транзакции.
    /// </summary>
    public Task VacuumAsync(CancellationToken cancellationToken = default)
        => Writer.EnqueueAsync(static connection => Execute(connection, "VACUUM;"), cancellationToken);

    /// <summary>
    /// Текущая версия схемы в файле.
    /// </summary>
    public int GetSchemaVersion()
    {
        using var connection = Connections.Open();
        return SqliteMigrator.GetVersion(connection);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _idleTimer?.Dispose();
        _writer?.Dispose();
        Connections.ClearPool();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_idleTimer is not null)
        {
            await _idleTimer.DisposeAsync().ConfigureAwait(false);
        }

        if (_writer is not null)
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
        }

        Connections.ClearPool();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private string QuarantineCorruptFile()
    {
        Connections.ClearPool();

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var directory = Path.GetDirectoryName(DatabasePath)!;
        var baseName = Path.GetFileNameWithoutExtension(DatabasePath);
        var extension = Path.GetExtension(DatabasePath);
        var corruptPath = Path.Combine(directory, $"{baseName}.corrupt-{stamp}{extension}");

        for (var attempt = 1; File.Exists(corruptPath); attempt++)
        {
            corruptPath = Path.Combine(directory, $"{baseName}.corrupt-{stamp}-{attempt.ToString(CultureInfo.InvariantCulture)}{extension}");
        }

        File.Move(DatabasePath, corruptPath);
        foreach (var suffix in SidecarSuffixes)
        {
            var sidecar = DatabasePath + suffix;
            if (File.Exists(sidecar))
            {
                File.Move(sidecar, corruptPath + suffix, overwrite: true);
            }
        }

        return corruptPath;
    }
}

/// <summary>
/// Итог инициализации базы.
/// </summary>
/// <param name="Path">Путь к файлу.</param>
/// <param name="SchemaVersion">Версия схемы после миграций.</param>
/// <param name="AppliedMigrations">Применённые в этот раз миграции; пусто, если схема была актуальна.</param>
/// <param name="WasCreated">Файл создан заново: его не было или он был повреждён.</param>
/// <param name="CorruptFilePath">Куда отложен повреждённый файл; <c>null</c>, если повреждения не было.</param>
public sealed record DatabaseInitialization(
    string Path,
    int SchemaVersion,
    IReadOnlyList<Migration> AppliedMigrations,
    bool WasCreated,
    string? CorruptFilePath)
{
    /// <summary>Повреждённый файл отложен, индекс нужно построить заново.</summary>
    public bool WasRecovered => CorruptFilePath is not null;
}

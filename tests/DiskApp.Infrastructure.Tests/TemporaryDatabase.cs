using DiskApp.Infrastructure.Sqlite;

using Microsoft.Data.Sqlite;

namespace DiskApp.Infrastructure.Tests;

/// <summary>
/// Временная база SQLite в собственной <see cref="TemporaryDirectory"/>: файл появляется при первом открытии соединения,
/// в <see cref="Dispose"/> пул соединений сбрасывается и папка удаляется вместе с файлами <c>-wal</c> и <c>-shm</c>.
/// </summary>
/// <remarks>
/// База файловая, а не <c>:memory:</c>, чтобы тесты видели то же поведение, что и приложение:
/// журнал WAL, несколько соединений к одному файлу, пул соединений Microsoft.Data.Sqlite.
/// Соединения открываются и пулы сбрасываются через <see cref="DiskApp.Infrastructure.Sqlite.SqliteConnectionFactory"/>:
/// тесты идут параллельно, и сброс пулов не должен совпасть с открытием соединения в соседнем тесте.
/// </remarks>
internal sealed class TemporaryDatabase : IDisposable
{
    private const string DatabaseFileName = "test.db";

    /// <summary>
    /// Создаёт папку и строку подключения; сам файл базы появится при первом <see cref="OpenConnection"/>.
    /// </summary>
    /// <param name="prefix">Префикс имени временной папки; по умолчанию <c>db</c>.</param>
    public TemporaryDatabase(string prefix = "db")
    {
        Directory = new TemporaryDirectory(prefix);
        FilePath = Directory.Resolve(DatabaseFileName);
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    /// <summary>
    /// Папка, в которой лежит база; подходит для файлов рядом с ней (резервные копии, экспорт).
    /// </summary>
    public TemporaryDirectory Directory { get; }

    /// <summary>
    /// Абсолютный путь к файлу базы.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Строка подключения к файлу базы: чтение и запись, создание при отсутствии.
    /// </summary>
    public string ConnectionString { get; }

    /// <summary>
    /// Открывает новое соединение. Закрыть его (<c>using</c>) — обязанность вызывающего.
    /// </summary>
    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(ConnectionString);
        try
        {
            SqliteConnectionFactory.OpenPooled(connection);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Сбрасывает все пулы соединений процесса (файл держат соединения из пула любой строки подключения, в том числе
    /// фабрики приложения с другими параметрами) и удаляет папку с базой. Повторный вызов безопасен.
    /// Соединения, занятые другими тестами, не трогаются: они закроются при возврате в пул.
    /// </summary>
    public void Dispose()
    {
        SqliteConnectionFactory.ClearAllPools();

        Directory.Dispose();
    }

    /// <inheritdoc />
    public override string ToString() => FilePath;
}

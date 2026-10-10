using Microsoft.Data.Sqlite;

namespace DiskApp.Testing;

/// <summary>
/// Временная база SQLite в собственной <see cref="TemporaryDirectory"/>: файл появляется при первом открытии соединения,
/// в <see cref="Dispose"/> пул соединений сбрасывается и папка удаляется вместе с файлами <c>-wal</c> и <c>-shm</c>.
/// </summary>
/// <remarks>
/// База файловая, а не <c>:memory:</c>, чтобы тесты видели то же поведение, что и приложение:
/// журнал WAL, несколько соединений к одному файлу, пул соединений Microsoft.Data.Sqlite.
/// </remarks>
public sealed class TemporaryDatabase : IDisposable
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
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Сбрасывает пул соединений к этой базе (иначе файл остаётся открытым) и удаляет папку с базой. Повторный вызов безопасен.
    /// </summary>
    public void Dispose()
    {
        using (var connection = new SqliteConnection(ConnectionString))
        {
            SqliteConnection.ClearPool(connection);
        }

        Directory.Dispose();
    }

    /// <inheritdoc />
    public override string ToString() => FilePath;
}

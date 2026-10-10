using DiskApp.Infrastructure.Sqlite;

using Dapper;

namespace DiskApp.Infrastructure.Tests.Sqlite;

/// <summary>
/// Фабрика соединений: PRAGMA из PLAN.md (этап 1.3) применяются к каждому соединению, путь абсолютный, пул сбрасывается.
/// </summary>
public sealed class SqliteConnectionFactoryTests
{
    [Fact]
    public void Open_NewConnection_AppliesPragmas()
    {
        // Arrange
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.FilePath);

        // Act
        using var connection = factory.Open();

        // Assert
        connection.ExecuteScalar<string>("PRAGMA journal_mode").ShouldBe("wal");
        connection.ExecuteScalar<long>("PRAGMA synchronous").ShouldBe(1, "NORMAL");
        connection.ExecuteScalar<long>("PRAGMA temp_store").ShouldBe(2, "MEMORY");
        connection.ExecuteScalar<long>("PRAGMA cache_size").ShouldBe(-65536);
        connection.ExecuteScalar<long>("PRAGMA mmap_size").ShouldBe(268435456);
        connection.ExecuteScalar<long>("PRAGMA foreign_keys").ShouldBe(1);
        connection.ExecuteScalar<long>("PRAGMA busy_timeout").ShouldBe(5000);
    }

    [Fact]
    public async Task OpenAsync_NewConnection_IsOpenAndPointsToFile()
    {
        // Arrange
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.FilePath);

        // Act
        await using var connection = await factory.OpenAsync(TestContext.Current.CancellationToken);

        // Assert
        connection.State.ShouldBe(System.Data.ConnectionState.Open);
        connection.DataSource.ShouldBe(database.FilePath);
        File.Exists(database.FilePath).ShouldBeTrue();
    }

    [Fact]
    public void Constructor_RelativePath_StoresFullPath()
    {
        // Act
        var factory = new SqliteConnectionFactory("relative.db");

        // Assert
        Path.IsPathRooted(factory.DatabasePath).ShouldBeTrue();
        factory.ConnectionString.ShouldContain("Mode=ReadWriteCreate");
    }

    [Fact]
    public void ClearPool_AfterUse_AllowsDeletingFile()
    {
        // Arrange
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.FilePath);
        using (var connection = factory.Open())
        {
            connection.Execute("CREATE TABLE t (x)");
        }

        // Act
        factory.ClearPool();

        // Assert
        Should.NotThrow(() => File.Delete(database.FilePath));
    }
}

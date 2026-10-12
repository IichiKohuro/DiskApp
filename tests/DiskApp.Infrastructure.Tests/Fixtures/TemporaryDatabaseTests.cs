using Dapper;

namespace DiskApp.Infrastructure.Tests.Fixtures;

/// <summary>
/// Проверки фикстуры <see cref="TemporaryDatabase"/>: файловая база создаётся по требованию,
/// переживает несколько соединений и бесследно удаляется вместе с журналом WAL и пулом соединений.
/// </summary>
public sealed class TemporaryDatabaseTests
{
    [Fact]
    public void Constructor_Always_PointsToFileInsideOwnDirectory()
    {
        // Arrange & Act
        using var database = new TemporaryDatabase();

        // Assert
        database.FilePath.ShouldStartWith(database.Directory.FullPath + Path.DirectorySeparatorChar);
        File.Exists(database.FilePath).ShouldBeFalse("файл появляется только при первом открытии соединения");
    }

    [Fact]
    public void OpenConnection_WriteThenReadFromSecondConnection_SeesCommittedRows()
    {
        // Arrange
        using var database = new TemporaryDatabase();

        // Act
        using (var writer = database.OpenConnection())
        {
            writer.Execute("PRAGMA journal_mode = WAL");
            writer.Execute("CREATE TABLE items(name TEXT NOT NULL)");
            writer.Execute("INSERT INTO items(name) VALUES (@Name)", new { Name = "report.pdf" });
        }

        using var reader = database.OpenConnection();
        var names = reader.Query<string>("SELECT name FROM items").ToArray();

        // Assert
        File.Exists(database.FilePath).ShouldBeTrue();
        names.ShouldBe(["report.pdf"]);
    }

    [Fact]
    public void Dispose_AfterWalConnections_RemovesDatabaseAndDirectory()
    {
        // Arrange
        using var database = new TemporaryDatabase();
        using (var connection = database.OpenConnection())
        {
            connection.Execute("PRAGMA journal_mode = WAL");
            connection.Execute("CREATE TABLE items(name TEXT NOT NULL)");
        }

        // Act
        database.Dispose();

        // Assert
        File.Exists(database.FilePath).ShouldBeFalse();
        Directory.Exists(database.Directory.FullPath).ShouldBeFalse();
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        // Arrange
        using var database = new TemporaryDatabase();
        database.Dispose();

        // Act & Assert
        Should.NotThrow(database.Dispose);
    }
}

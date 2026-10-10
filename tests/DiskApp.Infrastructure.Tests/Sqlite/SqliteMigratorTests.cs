using Dapper;

using DiskApp.Infrastructure.Sqlite;

namespace DiskApp.Infrastructure.Tests.Sqlite;

/// <summary>
/// Миграции: скрипты из ресурсов идут подряд, применяются один раз, схема V001 содержит все таблицы,
/// FTS синхронизируется триггерами, внешние ключи каскадируют.
/// </summary>
public sealed class SqliteMigratorTests
{
    private static readonly string[] ExpectedTables =
    [
        "schema_version", "roots", "entries", "entries_fts", "usage", "favorites", "tags", "entry_tags",
        "collections", "collection_items", "query_history", "meta", "scan_log",
    ];

    [Fact]
    public void Migrations_EmbeddedScripts_AreSequentialFromOne()
    {
        // Act
        var migrations = SqliteMigrator.Migrations;

        // Assert
        migrations.Count.ShouldBeGreaterThanOrEqualTo(1);
        migrations[0].Version.ShouldBe(1);
        migrations[0].Name.ShouldBe("init");
        migrations.Select(static migration => migration.Version).ShouldBe(Enumerable.Range(1, migrations.Count));
        SqliteMigrator.LatestVersion.ShouldBe(migrations.Count);
        migrations.ShouldAllBe(static migration => migration.Sql.Length > 0);
    }

    [Fact]
    public void GetVersion_EmptyDatabase_IsZero()
    {
        // Arrange
        using var database = new TemporaryDatabase();
        using var connection = new SqliteConnectionFactory(database.FilePath).Open();

        // Act & Assert
        SqliteMigrator.GetVersion(connection).ShouldBe(0);
    }

    [Fact]
    public void Apply_FreshDatabase_CreatesAllTablesAndRecordsVersions()
    {
        // Arrange
        using var database = new TemporaryDatabase();
        using var connection = new SqliteConnectionFactory(database.FilePath).Open();

        // Act
        var applied = SqliteMigrator.Apply(connection);

        // Assert
        applied.Select(static migration => migration.Version).ShouldBe(SqliteMigrator.Migrations.Select(static migration => migration.Version));
        SqliteMigrator.GetVersion(connection).ShouldBe(SqliteMigrator.LatestVersion);
        var tables = connection.Query<string>("SELECT name FROM sqlite_master WHERE type = 'table'").ToArray();
        foreach (var table in ExpectedTables)
        {
            tables.ShouldContain(table);
        }

        connection.Query<string>("SELECT name FROM schema_version ORDER BY version").ShouldBe(applied.Select(static migration => migration.Name));
    }

    [Fact]
    public void Apply_Twice_SecondRunAppliesNothing()
    {
        // Arrange
        using var database = new TemporaryDatabase();
        using var connection = new SqliteConnectionFactory(database.FilePath).Open();
        SqliteMigrator.Apply(connection);

        // Act
        var applied = SqliteMigrator.Apply(connection);

        // Assert
        applied.ShouldBeEmpty();
        SqliteMigrator.GetVersion(connection).ShouldBe(SqliteMigrator.LatestVersion);
    }

    [Fact]
    public void Apply_FromVersionOne_UpgradesToLatest()
    {
        // Arrange: база на первой версии схемы, как у пользователя старой сборки
        using var database = new TemporaryDatabase();
        using var connection = new SqliteConnectionFactory(database.FilePath).Open();
        SqliteMigrator.Apply(connection, targetVersion: 1);
        SqliteMigrator.GetVersion(connection).ShouldBe(1);

        // Act
        var applied = SqliteMigrator.Apply(connection);

        // Assert
        applied.Count.ShouldBe(SqliteMigrator.LatestVersion - 1);
        SqliteMigrator.GetVersion(connection).ShouldBe(SqliteMigrator.LatestVersion);
    }

    [Fact]
    public void Apply_TargetAboveLatest_Throws()
    {
        // Arrange
        using var database = new TemporaryDatabase();
        using var connection = new SqliteConnectionFactory(database.FilePath).Open();

        // Act & Assert
        Should.Throw<ArgumentOutOfRangeException>(() => SqliteMigrator.Apply(connection, SqliteMigrator.LatestVersion + 1));
    }

    [Fact]
    public void SchemaV001_FtsTriggers_KeepIndexInSyncOnInsertUpdateDelete()
    {
        // Arrange
        using var database = new TemporaryDatabase();
        using var connection = new SqliteConnectionFactory(database.FilePath).Open();
        SqliteMigrator.Apply(connection);
        var rootId = InsertRoot(connection);
        connection.Execute(
            """
            INSERT INTO entries (root_id, name, name_folded, ext, rel_path, created_utc, modified_utc)
            VALUES (@RootId, 'Quarterly Report.pdf', 'quarterly report.pdf', 'pdf', 'Quarterly Report.pdf', @Now, @Now)
            """,
            new { RootId = rootId, Now = SqliteTime.Format(DateTimeOffset.UtcNow) });

        // Act & Assert: вставка
        Match(connection, "port").ShouldBe(["Quarterly Report.pdf"]);

        // Act & Assert: переименование
        connection.Execute("UPDATE entries SET name = 'Отчёт за квартал.pdf', name_folded = 'отчёт за квартал.pdf' WHERE root_id = @RootId", new { RootId = rootId });
        Match(connection, "port").ShouldBeEmpty();
        Match(connection, "квартал").ShouldBe(["Отчёт за квартал.pdf"]);

        // Act & Assert: удаление
        connection.Execute("DELETE FROM entries WHERE root_id = @RootId", new { RootId = rootId });
        Match(connection, "квартал").ShouldBeEmpty();
    }

    [Fact]
    public void SchemaV001_DeleteRoot_CascadesToEntriesAndDependents()
    {
        // Arrange
        using var database = new TemporaryDatabase();
        using var connection = new SqliteConnectionFactory(database.FilePath).Open();
        SqliteMigrator.Apply(connection);
        var rootId = InsertRoot(connection);
        var now = SqliteTime.Format(DateTimeOffset.UtcNow);
        var entryId = connection.ExecuteScalar<long>(
            """
            INSERT INTO entries (root_id, name, name_folded, rel_path, created_utc, modified_utc)
            VALUES (@RootId, 'a.txt', 'a.txt', 'a.txt', @Now, @Now);
            SELECT last_insert_rowid();
            """,
            new { RootId = rootId, Now = now });
        connection.Execute("INSERT INTO usage (entry_id, open_count, last_opened_utc) VALUES (@EntryId, 1, @Now)", new { EntryId = entryId, Now = now });
        connection.Execute("INSERT INTO favorites (entry_id, sort_order, pinned_utc) VALUES (@EntryId, 0, @Now)", new { EntryId = entryId, Now = now });

        // Act
        connection.Execute("DELETE FROM roots WHERE id = @RootId", new { RootId = rootId });

        // Assert
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM entries").ShouldBe(0);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM usage").ShouldBe(0);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM favorites").ShouldBe(0);
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM entries_fts").ShouldBe(0);
    }

    [Fact]
    public void SchemaV001_DuplicateRelPathInRoot_IsRejected()
    {
        // Arrange
        using var database = new TemporaryDatabase();
        using var connection = new SqliteConnectionFactory(database.FilePath).Open();
        SqliteMigrator.Apply(connection);
        var rootId = InsertRoot(connection);
        var now = SqliteTime.Format(DateTimeOffset.UtcNow);
        const string insert = "INSERT INTO entries (root_id, name, name_folded, rel_path, created_utc, modified_utc) VALUES (@RootId, 'a', 'a', 'docs\\a', @Now, @Now)";
        connection.Execute(insert, new { RootId = rootId, Now = now });

        // Act & Assert
        Should.Throw<Microsoft.Data.Sqlite.SqliteException>(() => connection.Execute(insert, new { RootId = rootId, Now = now }))
            .SqliteExtendedErrorCode.ShouldBe(2067, "SQLITE_CONSTRAINT_UNIQUE");
    }

    private static long InsertRoot(System.Data.IDbConnection connection)
        => connection.ExecuteScalar<long>(
            "INSERT INTO roots (path, label, created_utc) VALUES ('D:\\Projects', 'Projects', @Now); SELECT last_insert_rowid();",
            new { Now = SqliteTime.Format(DateTimeOffset.UtcNow) });

    private static string[] Match(System.Data.IDbConnection connection, string term)
        => connection.Query<string>("SELECT name FROM entries_fts WHERE entries_fts MATCH @Term ORDER BY name", new { Term = $"\"{term}\"" }).ToArray();
}

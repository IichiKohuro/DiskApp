using Dapper;

using DiskApp.Infrastructure.Sqlite;

namespace DiskApp.Infrastructure.Tests.Sqlite;

/// <summary>
/// Файл индекса: создание и миграция при первом запуске, повторный запуск без изменений,
/// карантин повреждённого файла, обслуживание, порядок вызовов.
/// </summary>
public sealed class SqliteDatabaseTests
{
    [Fact]
    public async Task InitializeAsync_NewFile_CreatesSchemaAndStartsWriter()
    {
        // Arrange
        using var folder = new TemporaryDirectory();
        await using var database = new SqliteDatabase(folder.Resolve(Path.Combine("data", "index.db")));

        // Act
        var result = await database.InitializeAsync(TestContext.Current.CancellationToken);

        // Assert
        File.Exists(database.DatabasePath).ShouldBeTrue();
        result.WasCreated.ShouldBeTrue();
        result.WasRecovered.ShouldBeFalse();
        result.SchemaVersion.ShouldBe(SqliteMigrator.LatestVersion);
        result.AppliedMigrations.Count.ShouldBe(SqliteMigrator.Migrations.Count);
        database.GetSchemaVersion().ShouldBe(SqliteMigrator.LatestVersion);
        (await database.Writer.EnqueueAsync(connection => connection.ExecuteScalar<long>("SELECT COUNT(*) FROM roots"), TestContext.Current.CancellationToken)).ShouldBe(0);
    }

    [Fact]
    public async Task TryReleaseIdleMemory_OncePerIdlePeriod()
    {
        // Arrange
        using var folder = new TemporaryDirectory();
        await using var database = new SqliteDatabase(folder.Resolve("index.db")) { IdleDelay = TimeSpan.Zero };
        await database.InitializeAsync(TestContext.Current.CancellationToken);

        // Act
        var first = await database.ReleaseIdleMemoryAsync();
        var second = await database.ReleaseIdleMemoryAsync();
        using (database.Connections.Open())
        {
        }

        var afterActivity = await database.ReleaseIdleMemoryAsync();

        // Assert
        first.ShouldBeTrue();
        second.ShouldBeFalse("за один простой память отдаётся один раз");
        afterActivity.ShouldBeTrue("новое чтение начинает новый период простоя");
    }

    [Fact]
    public async Task TryReleaseIdleMemory_RecentActivity_KeepsCaches()
    {
        // Arrange
        using var folder = new TemporaryDirectory();
        await using var database = new SqliteDatabase(folder.Resolve("index.db")) { IdleDelay = TimeSpan.FromHours(1) };
        await database.InitializeAsync(TestContext.Current.CancellationToken);

        // Act & Assert
        (await database.ReleaseIdleMemoryAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task InitializeAsync_ExistingHealthyFile_AppliesNothing()
    {
        // Arrange
        using var folder = new TemporaryDirectory();
        var path = folder.Resolve("index.db");
        await using (var first = new SqliteDatabase(path))
        {
            await first.InitializeAsync(TestContext.Current.CancellationToken);
            await first.Writer.EnqueueAsync(connection => connection.Execute("INSERT INTO meta (key, value) VALUES ('k', 'v')"), TestContext.Current.CancellationToken);
        }

        await using var second = new SqliteDatabase(path);

        // Act
        var result = await second.InitializeAsync(TestContext.Current.CancellationToken);

        // Assert
        result.WasCreated.ShouldBeFalse();
        result.WasRecovered.ShouldBeFalse();
        result.AppliedMigrations.ShouldBeEmpty();
        using var reader = second.Connections.Open();
        reader.ExecuteScalar<string>("SELECT value FROM meta WHERE key = 'k'").ShouldBe("v");
    }

    [Fact]
    public async Task InitializeAsync_CorruptFile_QuarantinesItAndCreatesFreshDatabase()
    {
        // Arrange
        const string garbage = "this is definitely not a sqlite database, just text that is long enough";
        using var folder = new TemporaryDirectory();
        var path = folder.CreateFile("index.db", garbage);
        await using var database = new SqliteDatabase(path);

        // Act
        var result = await database.InitializeAsync(TestContext.Current.CancellationToken);

        // Assert
        result.WasRecovered.ShouldBeTrue();
        result.WasCreated.ShouldBeTrue();
        result.CorruptFilePath.ShouldNotBeNull();
        Path.GetFileName(result.CorruptFilePath).ShouldStartWith("index.corrupt-");
        Path.GetExtension(result.CorruptFilePath).ShouldBe(".db");
        File.ReadAllText(result.CorruptFilePath).ShouldBe(garbage, "повреждённый файл перемещён как есть, для разбора и восстановления");
        database.CheckIntegrity().ShouldBeTrue();
        database.GetSchemaVersion().ShouldBe(SqliteMigrator.LatestVersion);
    }

    [Fact]
    public async Task CheckIntegrity_HealthyAndCorrupt_ReportsAccordingly()
    {
        // Arrange
        using var folder = new TemporaryDirectory();
        await using var healthy = new SqliteDatabase(folder.Resolve("healthy.db"));
        await healthy.InitializeAsync(TestContext.Current.CancellationToken);
        var corrupt = new SqliteDatabase(folder.CreateFile("corrupt.db", new string('x', 4096)));

        // Act & Assert
        healthy.CheckIntegrity().ShouldBeTrue();
        corrupt.CheckIntegrity().ShouldBeFalse();
        corrupt.Dispose();
    }

    [Fact]
    public async Task OptimizeAndVacuum_AfterInitialization_Succeed()
    {
        // Arrange
        using var folder = new TemporaryDirectory();
        await using var database = new SqliteDatabase(folder.Resolve("index.db"));
        await database.InitializeAsync(TestContext.Current.CancellationToken);

        // Act & Assert
        await Should.NotThrowAsync(() => database.OptimizeAsync(TestContext.Current.CancellationToken));
        await Should.NotThrowAsync(() => database.VacuumAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Writer_BeforeInitialize_Throws()
    {
        // Arrange
        using var folder = new TemporaryDirectory();
        await using var database = new SqliteDatabase(folder.Resolve("index.db"));

        // Act & Assert
        Should.Throw<InvalidOperationException>(() => database.Writer);
    }

    [Fact]
    public async Task InitializeAsync_Twice_Throws()
    {
        // Arrange
        using var folder = new TemporaryDirectory();
        await using var database = new SqliteDatabase(folder.Resolve("index.db"));
        await database.InitializeAsync(TestContext.Current.CancellationToken);

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(() => database.InitializeAsync(TestContext.Current.CancellationToken));
    }
}

using DiskApp.Infrastructure.Sqlite;

using Dapper;

namespace DiskApp.Infrastructure.Tests.Sqlite;

/// <summary>
/// Очередь писателя: выделенный поток и одно соединение, порядок выполнения, изоляция ошибок,
/// отмена, остановка и параллельное чтение в WAL во время открытой транзакции писателя.
/// </summary>
public sealed class SqliteWriterTests
{
    [Fact]
    public async Task EnqueueAsync_Job_RunsOnWriterThreadAndReturnsResult()
    {
        // Arrange
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.FilePath);
        await using var queue = new SqliteWriter(factory);

        // Act
        var (threadName, value) = await queue.EnqueueAsync(connection =>
            (Thread.CurrentThread.Name, connection.ExecuteScalar<long>("SELECT 40 + 2")), TestContext.Current.CancellationToken);

        // Assert
        threadName.ShouldBe("DiskApp SQLite writer");
        value.ShouldBe(42);
        queue.PendingCount.ShouldBe(0);
    }

    [Fact]
    public async Task EnqueueAsync_ManyJobs_RunInEnqueueOrderOnOneConnection()
    {
        // Arrange
        using var database = new TemporaryDatabase();
        await using var queue = new SqliteWriter(new SqliteConnectionFactory(database.FilePath));
        await queue.EnqueueAsync(connection => connection.Execute("CREATE TABLE log (n INTEGER)"), TestContext.Current.CancellationToken);

        // Act
        var tasks = Enumerable.Range(0, 200)
            .Select(n => queue.EnqueueAsync(connection => connection.Execute("INSERT INTO log (n) VALUES (@N)", new { N = n }), TestContext.Current.CancellationToken))
            .ToArray();
        await Task.WhenAll(tasks);

        // Assert
        using var reader = database.OpenConnection();
        reader.Query<long>("SELECT n FROM log ORDER BY rowid").ShouldBe(Enumerable.Range(0, 200).Select(static n => (long)n));
    }

    [Fact]
    public async Task EnqueueAsync_JobThrows_ExceptionReachesCallerAndQueueContinues()
    {
        // Arrange
        using var database = new TemporaryDatabase();
        await using var queue = new SqliteWriter(new SqliteConnectionFactory(database.FilePath));

        // Act
        var failing = queue.EnqueueAsync(connection => connection.Execute("INSERT INTO missing_table VALUES (1)"), TestContext.Current.CancellationToken);
        var next = queue.EnqueueAsync(connection => connection.ExecuteScalar<long>("SELECT 1"), TestContext.Current.CancellationToken);

        // Assert
        await Should.ThrowAsync<Microsoft.Data.Sqlite.SqliteException>(failing);
        (await next).ShouldBe(1);
    }

    [Fact]
    public async Task EnqueueAsync_CancelledBeforeExecution_IsCancelled()
    {
        // Arrange
        using var database = new TemporaryDatabase();
        await using var queue = new SqliteWriter(new SqliteConnectionFactory(database.FilePath));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act
        var task = queue.EnqueueAsync(connection => 1, cancellation.Token);

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(task);
    }

    [Fact]
    public async Task Dispose_WithPendingJobs_CompletesThemAndRejectsNewOnes()
    {
        // Arrange
        using var database = new TemporaryDatabase();
        var queue = new SqliteWriter(new SqliteConnectionFactory(database.FilePath));
        var pending = Enumerable.Range(0, 20)
            .Select(_ => queue.EnqueueAsync(connection => connection.ExecuteScalar<long>("SELECT 7"), TestContext.Current.CancellationToken))
            .ToArray();

        // Act
        await queue.DisposeAsync();

        // Assert
        queue.IsDisposed.ShouldBeTrue();
        (await Task.WhenAll(pending)).ShouldAllBe(static value => value == 7);
        Should.Throw<ObjectDisposedException>(() => queue.EnqueueAsync(static _ => 0));
        Should.NotThrow(queue.Dispose);
    }

    [Fact]
    public async Task Reader_WhileWriterTransactionOpen_SeesSnapshotWithoutBlocking()
    {
        // Arrange
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.FilePath);
        await using var queue = new SqliteWriter(factory);
        await queue.EnqueueAsync(connection => connection.Execute("CREATE TABLE items (x INTEGER)"), TestContext.Current.CancellationToken);
        using var inserted = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        // Act
        var write = queue.EnqueueAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            connection.Execute("INSERT INTO items (x) VALUES (1)", transaction: transaction);
            inserted.Set();
            release.Wait(TimeSpan.FromSeconds(10), CancellationToken.None);
            transaction.Commit();
            return true;
        }, TestContext.Current.CancellationToken);

        inserted.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ShouldBeTrue();
        long duringTransaction;
        using (var reader = factory.Open())
        {
            duringTransaction = reader.ExecuteScalar<long>("SELECT COUNT(*) FROM items");
        }

        release.Set();
        await write;

        long afterCommit;
        using (var reader = factory.Open())
        {
            afterCommit = reader.ExecuteScalar<long>("SELECT COUNT(*) FROM items");
        }

        // Assert
        duringTransaction.ShouldBe(0, "читатель в WAL видит снимок до коммита и не ждёт писателя");
        afterCommit.ShouldBe(1);
    }
}

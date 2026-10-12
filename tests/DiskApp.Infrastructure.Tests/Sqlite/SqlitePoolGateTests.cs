using System.Data;

using DiskApp.Infrastructure.Sqlite;

using Microsoft.Data.Sqlite;

namespace DiskApp.Infrastructure.Tests.Sqlite;

/// <summary>
/// Общая блокировка пула в <see cref="DiskApp.Infrastructure.Sqlite.SqliteConnectionFactory"/>: пока соединение выдаётся
/// из пула, очистка пулов и другие открытия ждут. Microsoft.Data.Sqlite 10.0.12 в этот миг уже пометил соединение активным, но ещё не запомнил владельца
/// (dotnet/efcore#39008); очистка пула или чужое открытие закрывали соединение под писателем или отдавали его второму
/// владельцу.
/// </summary>
[Collection(SqlitePoolGate.Name)]
public sealed class SqlitePoolGateTests
{
    // Сколько соперник ждёт внутри открытия: без общей блокировки очистка или открытие заканчиваются за миллисекунды.
    private static readonly TimeSpan RivalWindow = TimeSpan.FromMilliseconds(200);

    [Theory]
    [InlineData("ClearPool")]
    [InlineData("ClearAllPools")]
    [InlineData("Open")]
    public async Task OpenPooled_RivalStartsDuringOpen_RivalWaitsUntilConnectionIsOpen(string rival)
    {
        // Arrange: соперник стартует изнутри Open, когда соединение уже выдано из пула
        using var database = new TemporaryDatabase();
        var factory = new SqliteConnectionFactory(database.FilePath);
        using var connection = new SqliteConnection(factory.ConnectionString);
        using var rivalFinished = new ManualResetEventSlim();
        Task? rivalTask = null;
        var finishedDuringOpen = false;
        connection.StateChange += (_, change) =>
        {
            if (change.CurrentState != ConnectionState.Open)
            {
                return;
            }

            rivalTask = Task.Factory.StartNew(
                () =>
                {
                    RunRival(rival, factory);
                    rivalFinished.Set();
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            finishedDuringOpen = rivalFinished.Wait(RivalWindow, TestContext.Current.CancellationToken);
        };

        // Act
        SqliteConnectionFactory.OpenPooled(connection);
        await rivalTask!.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Assert
        finishedDuringOpen.ShouldBeFalse($"{rival} не должен выполняться, пока соединение выдаётся из пула");
    }

    private static void RunRival(string rival, SqliteConnectionFactory factory)
    {
        switch (rival)
        {
            case "ClearPool":
                factory.ClearPool();
                break;
            case "ClearAllPools":
                SqliteConnectionFactory.ClearAllPools();
                break;
            default:
                factory.Open().Dispose();
                break;
        }
    }
}

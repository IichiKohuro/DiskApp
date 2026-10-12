using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

using DiskApp.Client;
using DiskApp.Contracts.Entries;
using DiskApp.Contracts.Hub;
using DiskApp.Contracts.Operations;
using DiskApp.Contracts.Roots;
using DiskApp.Infrastructure;
using DiskApp.Server.Tests.Api;

using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace DiskApp.Server.Tests.Client;

/// <summary>
/// Клиент хаба против настоящего сервера (PLAN.md, этапы 5.2 и 5.4): состояние при подключении, буфер событий до
/// подписки, изменения подписанного корня, события операций, переподключение после остановки сервера с восстановлением
/// подписок.
/// </summary>
public sealed class ClientHubTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Start_StatusIsBufferedUntilFirstSubscriber()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var root = await context.AddRootAsync();
        await using var services = ClientApiTests.Services(context);
        var hub = services.GetRequiredService<DiskAppHubConnection>();
        var states = new ConcurrentQueue<HubConnectionState>();
        hub.StateChanged += (_, state) => states.Enqueue(state);

        // Act
        await hub.StartAsync(TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var received = new TaskCompletionSource<HubStatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.StatusReceived += status => received.TrySetResult(status);
        var status = await received.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        var invoked = await hub.GetStatusAsync(TestContext.Current.CancellationToken);

        // Assert
        hub.State.ShouldBe(HubConnectionState.Connected);
        states.ShouldContain(HubConnectionState.Connected);
        status.Index.Roots.ShouldHaveSingleItem().RootId.ShouldBe(root.Id);
        invoked.Index.State.ShouldBe(IndexerState.Idle);
    }

    [Fact]
    public async Task SubscribedRootAndOperations_RaiseTypedEvents()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        context.Files.CreateDirectory("docs");
        var source = context.Files.CreateFile("report.txt", "r");
        var root = await context.AddRootAsync();
        await using var services = ClientApiTests.Services(context);
        var api = services.GetRequiredService<IDiskAppApi>();
        var hub = services.GetRequiredService<DiskAppHubConnection>();
        var changed = new TaskCompletionSource<EntriesChangedDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<OperationCompletedDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.EntriesChanged += batch =>
        {
            if (batch.Changes.Any(static change => change.RelPath == "docs"))
            {
                changed.TrySetResult(batch);
            }
        };
        hub.OperationCompleted += done => completed.TrySetResult(done);
        await hub.StartAsync(TestContext.Current.CancellationToken);
        await hub.SubscribeRootAsync(root.Id, TestContext.Current.CancellationToken);

        // Act
        var operation = await api.EnqueueOperationAsync(new OperationRequest { Kind = OperationKind.Copy, Sources = [source], Destination = context.Files.Resolve("docs") },
            TestContext.Current.CancellationToken);
        var done = await completed.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        var batch = await changed.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);

        // Assert
        done.OperationId.ShouldBe(operation.Id);
        done.State.ShouldBe(OperationState.Completed);
        batch.RootId.ShouldBe(root.Id);
        hub.Subscriptions.ShouldBe([root.Id]);
    }

    [Fact]
    public async Task ServerRestart_ClientReconnectsAndRestoresSubscriptions()
    {
        // Arrange
        using var data = new TemporaryDirectory("client-reconnect");
        using var files = new TemporaryDirectory("client-reconnect-files");
        files.CreateDirectory("docs");
        var port = FreePort();
        var options = new DiskAppServerOptions { Paths = AppPaths.ForDataDirectory(data.FullPath), Url = $"http://127.0.0.1:{port}" };
        var first = DiskAppServerHost.Create(options);
        await first.StartAsync(TestContext.Current.CancellationToken);
        await using var services = new ServiceCollection()
            .AddDiskAppClient(new DiskAppClientOptions
            {
                Endpoint = new ServerEndpoint(first.BaseAddress!),
                Hub = new DiskAppHubOptions { RetryDelays = [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250)] },
            })
            .BuildServiceProvider();
        var api = services.GetRequiredService<IDiskAppApi>();
        var hub = services.GetRequiredService<DiskAppHubConnection>();
        var root = await api.CreateRootAsync(new Contracts.Roots.CreateRootRequest { Path = files.FullPath }, TestContext.Current.CancellationToken);
        var states = new ConcurrentQueue<HubConnectionState>();
        var statuses = 0;
        hub.StateChanged += (_, state) => states.Enqueue(state);
        hub.StatusReceived += _ => Interlocked.Increment(ref statuses);
        await hub.StartAsync(TestContext.Current.CancellationToken);
        await hub.SubscribeRootAsync(root.Id, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => Volatile.Read(ref statuses) >= 1);

        // Act: сервер останавливается и поднимается снова на том же адресе
        await first.StopAsync(TestContext.Current.CancellationToken);
        await first.DisposeAsync();
        await WaitUntilAsync(() => states.Contains(HubConnectionState.Reconnecting));
        await using var second = DiskAppServerHost.Create(options);
        await second.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => hub.State == HubConnectionState.Connected && Volatile.Read(ref statuses) >= 2);
        var changed = new TaskCompletionSource<EntriesChangedDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.EntriesChanged += batch => changed.TrySetResult(batch);
        await second.Services.GetRequiredService<Core.Indexing.IndexerService>().WhenIdleAsync(TestContext.Current.CancellationToken).WaitAsync(Wait, TestContext.Current.CancellationToken);
        files.CreateFile(Path.Combine("docs", "after-restart.txt"), "a");
        var batch = await changed.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        await second.StopAsync(TestContext.Current.CancellationToken);
        DiskApp.Infrastructure.Sqlite.SqliteConnectionFactory.ClearAllPools();

        // Assert
        states.ShouldContain(HubConnectionState.Reconnecting);
        batch.RootId.ShouldBe(root.Id, "подписка на корень восстановлена после переподключения");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        condition().ShouldBeTrue("условие не выполнилось за отведённое время");
    }
}

using System.Collections.Concurrent;
using System.Text;

using DiskApp.Contracts;
using DiskApp.Contracts.App;
using DiskApp.Contracts.Entries;
using DiskApp.Contracts.Hub;
using DiskApp.Contracts.Roots;
using DiskApp.Core.Events;
using DiskApp.Server.Realtime;
using DiskApp.Server.Tests.Api;

using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

using EntryChangeKind = DiskApp.Contracts.Entries.EntryChangeKind;
using IndexPhase = DiskApp.Contracts.Roots.IndexPhase;

namespace DiskApp.Server.Tests.Realtime;

/// <summary>
/// Хаб событий (PLAN.md, этап 4.4): состояние при подключении, подписка на корень, ход индексации и смены состояния,
/// настройки, пакеты изменений с ограничением размера и сводкой при лавине.
/// </summary>
public sealed class HubTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Connect_ReceivesStatusFirstAndGetStatusMatches()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var root = await context.AddRootAsync();
        var status = new TaskCompletionSource<HubStatusDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = Connect(context);
        connection.On<HubStatusDto>(nameof(IDiskAppHubClient.OnStatus), dto => status.TrySetResult(dto));

        // Act
        await connection.StartAsync(TestContext.Current.CancellationToken);
        var pushed = await status.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        var invoked = await connection.InvokeAsync<HubStatusDto>(HubRoutes.GetStatus, TestContext.Current.CancellationToken);

        // Assert
        pushed.Index.Roots.ShouldHaveSingleItem().RootId.ShouldBe(root.Id);
        pushed.Index.State.ShouldBe(IndexerState.Idle);
        pushed.Operations.ShouldBeEmpty();
        invoked.Index.Roots.ShouldHaveSingleItem().Status.ShouldBe(RootStatus.Online);
    }

    [Fact]
    public async Task SubscribeRoot_FileCreated_DeliversChangesOnlyToSubscribers()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        context.Files.CreateDirectory("docs");
        var root = await context.AddRootAsync();
        var received = new TaskCompletionSource<EntriesChangedDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var strangerMessages = 0;
        await using var subscriber = Connect(context);
        await using var stranger = Connect(context);
        subscriber.On<EntriesChangedDto>(nameof(IDiskAppHubClient.OnEntriesChanged), batch =>
        {
            if (batch.Changes.Any(static change => change.RelPath == "docs"))
            {
                received.TrySetResult(batch);
            }
        });
        stranger.On<EntriesChangedDto>(nameof(IDiskAppHubClient.OnEntriesChanged), _ => Interlocked.Increment(ref strangerMessages));
        await subscriber.StartAsync(TestContext.Current.CancellationToken);
        await stranger.StartAsync(TestContext.Current.CancellationToken);
        await subscriber.InvokeAsync(HubRoutes.SubscribeRoot, root.Id, TestContext.Current.CancellationToken);

        // Act
        context.Files.CreateFile(Path.Combine("docs", "fresh.txt"), "new");
        var batch = await received.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        // Assert: индексатор сообщает об изменении содержимого папки, клиент перечитывает её
        batch.RootId.ShouldBe(root.Id);
        batch.Changes.ShouldContain(static change => change.RelPath == "docs" && change.Kind == EntryChangeKind.Modified && change.IsDirectory);
        strangerMessages.ShouldBe(0);
    }

    [Fact]
    public async Task AddRoot_StreamsProgressAndStatusChanges()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        for (var i = 0; i < 20; i++)
        {
            context.Files.CreateFile($"file-{i}.txt", "x");
        }

        var progress = new ConcurrentQueue<IndexProgressDto>();
        var statuses = new ConcurrentQueue<RootStatusChangedDto>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = Connect(context);
        connection.On<IndexProgressDto>(nameof(IDiskAppHubClient.OnIndexProgress), item =>
        {
            progress.Enqueue(item);
            if (item.Phase == IndexPhase.Done)
            {
                done.TrySetResult();
            }
        });
        connection.On<RootStatusChangedDto>(nameof(IDiskAppHubClient.OnRootStatusChanged), statuses.Enqueue);
        await connection.StartAsync(TestContext.Current.CancellationToken);

        // Act
        var root = await context.AddRootAsync();
        await done.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        await Task.Delay(400, TestContext.Current.CancellationToken);

        // Assert
        progress.ShouldAllBe(item => item.RootId == root.Id);
        progress.Last().Written.ShouldBeGreaterThanOrEqualTo(20);
        statuses.Select(static status => status.Status).ShouldContain(RootStatus.Scanning);
        statuses.Last().Status.ShouldBe(RootStatus.Online);
    }

    [Fact]
    public async Task PatchSettings_BroadcastsNewSettings()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var received = new TaskCompletionSource<SettingsDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = Connect(context);
        connection.On<SettingsDto>(nameof(IDiskAppHubClient.OnSettingsChanged), settings => received.TrySetResult(settings));
        await connection.StartAsync(TestContext.Current.CancellationToken);

        // Act
        using var response = await context.Client.PatchAsync(new Uri(ApiRoutes.V1.Settings, UriKind.Relative),
            new StringContent("""{"search":{"pageSize":75}}""", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        var settings = await received.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);

        // Assert
        response.EnsureSuccessStatusCode();
        settings.Search.PageSize.ShouldBe(75);
    }

    [Fact]
    public async Task ManyChanges_ArriveInBoundedBatches()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync(withPlatform: true, environment: "Production",
            static services => services.AddSingleton(new HubBroadcastOptions { BatchInterval = TimeSpan.FromMilliseconds(200), MaxChangesPerMessage = 100 }));
        var batches = new ConcurrentQueue<EntriesChangedDto>();
        await using var connection = Connect(context);
        connection.On<EntriesChangedDto>(nameof(IDiskAppHubClient.OnEntriesChanged), batches.Enqueue);
        await connection.StartAsync(TestContext.Current.CancellationToken);
        await connection.InvokeAsync(HubRoutes.SubscribeRoot, 42L, TestContext.Current.CancellationToken);
        var bus = context.Host.Services.GetRequiredService<IIndexEventBus>();

        // Act
        for (var i = 0; i < 25; i++)
        {
            bus.Publish(new EntriesChangedEvent(42, Enumerable.Range(0, 10).Select(j => new EntryChange(Core.Events.EntryChangeKind.Added, $"f{i}-{j}.txt", (i * 10) + j, false)).ToArray()));
        }

        await WaitUntilAsync(() => batches.Sum(static batch => batch.Changes.Count) >= 250);

        // Assert
        batches.Sum(static batch => batch.Changes.Count).ShouldBe(250);
        batches.ShouldAllBe(batch => batch.Changes.Count <= 100 && batch.RootId == 42);
        batches.Count.ShouldBeLessThanOrEqualTo(6, "события склеиваются в пакеты, а не уходят по одному");
    }

    [Fact]
    public async Task ChangeFlood_CollapsesIntoReloadSignal()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync(withPlatform: true, environment: "Production",
            static services => services.AddSingleton(new HubBroadcastOptions { BatchInterval = TimeSpan.FromMilliseconds(300), MaxPendingChangesPerRoot = 10 }));
        var batches = new ConcurrentQueue<EntriesChangedDto>();
        await using var connection = Connect(context);
        connection.On<EntriesChangedDto>(nameof(IDiskAppHubClient.OnEntriesChanged), batches.Enqueue);
        await connection.StartAsync(TestContext.Current.CancellationToken);
        await connection.InvokeAsync(HubRoutes.SubscribeRoot, 7L, TestContext.Current.CancellationToken);
        var bus = context.Host.Services.GetRequiredService<IIndexEventBus>();

        // Act
        bus.Publish(new EntriesChangedEvent(7, Enumerable.Range(0, 50).Select(static j => new EntryChange(Core.Events.EntryChangeKind.Modified, $"m{j}.txt", j, false)).ToArray()));
        await WaitUntilAsync(() => !batches.IsEmpty);
        await Task.Delay(400, TestContext.Current.CancellationToken);

        // Assert
        var change = batches.ShouldHaveSingleItem().Changes.ShouldHaveSingleItem();
        change.Kind.ShouldBe(EntryChangeKind.Modified);
        change.RelPath.ShouldBeEmpty();
        change.IsDirectory.ShouldBeTrue();
    }

    private static HubConnection Connect(ServerTestContext context)
        => new HubConnectionBuilder()
            .WithUrl(new Uri(context.Host.BaseAddress!, HubRoutes.Path))
            .AddJsonProtocol(static json => json.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, DiskAppJsonContext.Default))
            .Build();

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

using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using DiskApp.Client;
using DiskApp.Contracts.Library;
using DiskApp.Contracts.Operations;
using DiskApp.Contracts.Roots;
using DiskApp.Contracts.Search;
using DiskApp.Infrastructure;
using DiskApp.Server.Tests.Api;

using Microsoft.Extensions.DependencyInjection;

namespace DiskApp.Server.Tests.Client;

/// <summary>
/// REST-клиент против настоящего сервера (PLAN.md, этап 5.4): сценарии от корня до настроек, ошибки ProblemDetails,
/// отмена, повторы только для GET, загрузка и скачивание с прогрессом, адрес сервера внутри процесса, токен сессии.
/// </summary>
public sealed class ClientApiTests
{
    [Fact]
    public async Task Scenario_RootSearchEntryLibrarySettings_WorksThroughClient()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        context.Files.CreateFile(Path.Combine("docs", "Годовой отчёт.pdf"), "pdf");
        context.Files.CreateFile(Path.Combine("docs", "notes.txt"), "n");
        using var services = Services(context);
        var api = services.GetRequiredService<IDiskAppApi>();

        // Act
        var health = await api.GetHealthAsync(TestContext.Current.CancellationToken);
        var root = await api.CreateRootAsync(new CreateRootRequest { Path = context.Files.FullPath, Label = "Файлы" }, TestContext.Current.CancellationToken);
        await context.WaitIdleAsync();
        var search = await api.SearchAsync(new SearchRequest { Q = "отчет", Limit = 10 }, TestContext.Current.CancellationToken);
        var hit = search.Items.ShouldHaveSingleItem();
        var entry = await api.GetEntryAsync(hit.Id, TestContext.Current.CancellationToken);
        var browse = await api.BrowseAsync(Path.Combine(context.Files.FullPath, "docs"), new BrowseOptions(SearchSort.Name, false), TestContext.Current.CancellationToken);
        var children = await api.GetChildrenAsync(browse.Folder!.Id, cancellationToken: TestContext.Current.CancellationToken);
        await api.AddFavoriteAsync(hit.Id, TestContext.Current.CancellationToken);
        var tag = await api.CreateTagAsync(new TagRequest { Name = "важное" }, TestContext.Current.CancellationToken);
        var tagged = await api.SetEntryTagsAsync(hit.Id, [tag.Id], TestContext.Current.CancellationToken);
        await api.OpenAsync(hit.Id, "отчет", TestContext.Current.CancellationToken);
        var history = await api.GetHistoryAsync(cancellationToken: TestContext.Current.CancellationToken);
        var recent = await api.GetRecentAsync(cancellationToken: TestContext.Current.CancellationToken);
        var favorites = await api.GetFavoritesAsync(TestContext.Current.CancellationToken);
        var collection = await api.CreateCollectionAsync(new CollectionRequest { Name = "Отчёты", Kind = CollectionKind.Manual }, TestContext.Current.CancellationToken);
        await api.SetCollectionItemsAsync(collection.Id, [hit.Id], TestContext.Current.CancellationToken);
        var items = await api.GetCollectionItemsAsync(collection.Id, TestContext.Current.CancellationToken);
        var resolved = await api.ResolveEntriesAsync([hit.FullPath, Path.Combine(context.Files.FullPath, "missing.txt")], TestContext.Current.CancellationToken);
        var settings = await api.PatchSettingsAsync(new JsonObject { ["search"] = new JsonObject { ["pageSize"] = 80 } }, TestContext.Current.CancellationToken);
        var status = await api.GetIndexStatusAsync(TestContext.Current.CancellationToken);
        var roots = await api.GetRootsAsync(TestContext.Current.CancellationToken);

        // Assert
        health.Status.ShouldBe("ok");
        root.Label.ShouldBe("Файлы");
        entry.Name.ShouldBe("Годовой отчёт.pdf");
        browse.Items.Select(static item => item.Name).ShouldBe(["notes.txt", "Годовой отчёт.pdf"], "порядковое сравнение: латиница раньше кириллицы");
        children.Items.Count.ShouldBe(2);
        tagged.Tags.ShouldNotBeNull().ShouldHaveSingleItem().Name.ShouldBe("важное");
        history.ShouldHaveSingleItem().Query.ShouldBe("отчет");
        recent.ShouldHaveSingleItem().Id.ShouldBe(hit.Id);
        favorites.ShouldHaveSingleItem().IsFavorite.ShouldBeTrue();
        items.ShouldHaveSingleItem().Id.ShouldBe(hit.Id);
        resolved.Entries.ShouldHaveSingleItem().Id.ShouldBe(hit.Id);
        resolved.Missing.ShouldHaveSingleItem().ShouldEndWith("missing.txt");
        settings.Search.PageSize.ShouldBe(80);
        status.State.ShouldBe(IndexerState.Idle);
        roots.ShouldHaveSingleItem().Id.ShouldBe(root.Id);
        context.Shell!.Calls.ShouldContain(call => call.Action == "open");
    }

    [Fact]
    public async Task Errors_BecomeApiExceptionWithCodeAndFieldErrors()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        using var services = Services(context);
        var api = services.GetRequiredService<IDiskAppApi>();
        await api.CreateRootAsync(new CreateRootRequest { Path = context.Files.FullPath }, TestContext.Current.CancellationToken);

        // Act
        var duplicate = await Should.ThrowAsync<DiskAppApiException>(() => api.CreateRootAsync(new CreateRootRequest { Path = context.Files.FullPath }, TestContext.Current.CancellationToken));
        var invalid = await Should.ThrowAsync<DiskAppApiException>(() => api.CreateRootAsync(new CreateRootRequest { Path = "relative" }, TestContext.Current.CancellationToken));
        var missing = await Should.ThrowAsync<DiskAppApiException>(() => api.GetEntryAsync(987654, TestContext.Current.CancellationToken));
        var notImplemented = await Should.ThrowAsync<DiskAppApiException>(() => api.GetOperationAsync(Guid.NewGuid(), TestContext.Current.CancellationToken));

        // Assert
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        duplicate.Code.ShouldBe("root.duplicate");
        duplicate.Message.ShouldContain("уже добавлен");
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        invalid.Errors.ShouldContainKey("Path");
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        notImplemented.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Search_CancelledToken_ThrowsOperationCanceled()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        using var services = Services(context);
        var api = services.GetRequiredService<IDiskAppApi>();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act & Assert
        await Should.ThrowAsync<OperationCanceledException>(() => api.SearchAsync(new SearchRequest { Q = "x" }, cancellation.Token));
    }

    [Fact]
    public async Task Retries_OnlyIdempotentGet()
    {
        // Arrange
        var flaky = new FlakyHandler(failures: 2);
        using var services = new ServiceCollection()
            .AddDiskAppClient(new DiskAppClientOptions { Endpoint = new ServerEndpoint(new Uri("http://127.0.0.1:9/")), RetryDelay = TimeSpan.FromMilliseconds(1) })
            .AddHttpClient<IDiskAppApi, HttpDiskAppApi>().ConfigurePrimaryHttpMessageHandler(() => flaky).Services
            .BuildServiceProvider();
        var api = services.GetRequiredService<IDiskAppApi>();

        // Act
        var health = await api.GetHealthAsync(TestContext.Current.CancellationToken);
        var getAttempts = flaky.Attempts;
        flaky.Reset(failures: 2);
        var post = await Should.ThrowAsync<DiskAppApiException>(() => api.RebuildIndexAsync(TestContext.Current.CancellationToken));

        // Assert
        health.Status.ShouldBe("ok");
        getAttempts.ShouldBe(3, "две неудачи и успешный повтор");
        post.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        flaky.Attempts.ShouldBe(1, "POST не повторяется");
    }

    [Fact]
    public async Task UploadAndDownload_ReportProgress()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var folder = context.Files.CreateDirectory("inbox");
        await context.AddRootAsync();
        using var services = Services(context);
        var api = services.GetRequiredService<IDiskAppApi>();
        var payload = Encoding.UTF8.GetBytes(new string('ж', 100_000));
        var uploaded = new List<long>();
        var downloaded = new List<long>();

        // Act
        var result = await api.UploadAsync(folder, [new UploadFile("big.txt", () => new MemoryStream(payload), payload.Length), new UploadFile("small.txt", () => new MemoryStream([1, 2, 3]))],
            ConflictPolicy.KeepBoth, new SyncProgress(uploaded), TestContext.Current.CancellationToken);
        await context.WaitIdleAsync();
        var entry = (await api.SearchAsync(new SearchRequest { Q = "big" }, TestContext.Current.CancellationToken)).Items.ShouldHaveSingleItem();
        using var copy = new MemoryStream();
        await api.DownloadContentAsync(entry.Id, copy, new SyncProgress(downloaded), TestContext.Current.CancellationToken);

        // Assert
        result.Saved.Count.ShouldBe(2);
        uploaded[^1].ShouldBe(payload.Length + 3);
        copy.ToArray().ShouldBe(payload);
        downloaded[^1].ShouldBe(payload.Length);
    }

    [Fact]
    public async Task InProcessEndpoint_RequestsWaitUntilServerIsReady()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        using var services = new ServiceCollection().AddDiskAppClient().BuildServiceProvider();
        var api = services.GetRequiredService<IDiskAppApi>();
        var provider = services.GetRequiredService<InProcessServerEndpointProvider>();

        // Act
        var pending = api.GetHealthAsync(TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var waitedForEndpoint = !pending.IsCompleted;
        provider.SetEndpoint(new ServerEndpoint(context.Host.BaseAddress!));
        var health = await pending.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert
        waitedForEndpoint.ShouldBeTrue();
        provider.IsReady.ShouldBeTrue();
        health.Status.ShouldBe("ok");
    }

    [Fact]
    public async Task SessionToken_IsSentByClient()
    {
        // Arrange
        using var data = new TemporaryDirectory("client-token");
        var token = DiskAppServerOptions.CreateSessionToken();
        await using var host = DiskAppServerHost.Create(new DiskAppServerOptions { Paths = AppPaths.ForDataDirectory(data.FullPath), StartIndexer = false, SessionToken = token });
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var withToken = new ServiceCollection().AddDiskAppClient(new DiskAppClientOptions { Endpoint = new ServerEndpoint(host.BaseAddress!, token) }).BuildServiceProvider();
        using var withoutToken = new ServiceCollection().AddDiskAppClient(new DiskAppClientOptions { Endpoint = new ServerEndpoint(host.BaseAddress!) }).BuildServiceProvider();

        // Act
        var roots = await withToken.GetRequiredService<IDiskAppApi>().GetRootsAsync(TestContext.Current.CancellationToken);
        var denied = await Should.ThrowAsync<DiskAppApiException>(() => withoutToken.GetRequiredService<IDiskAppApi>().GetRootsAsync(TestContext.Current.CancellationToken));
        await host.StopAsync(TestContext.Current.CancellationToken);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // Assert
        roots.ShouldBeEmpty();
        denied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    internal static ServiceProvider Services(ServerTestContext context, DiskAppClientOptions? options = null)
        => new ServiceCollection()
            .AddDiskAppClient((options ?? new DiskAppClientOptions()) with { Endpoint = new ServerEndpoint(context.Host.BaseAddress!) })
            .BuildServiceProvider();

    private sealed class FlakyHandler(int failures) : HttpMessageHandler
    {
        private int _failures = failures;

        public int Attempts { get; private set; }

        public void Reset(int failures)
        {
            _failures = failures;
            Attempts = 0;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            if (_failures-- > 0)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"status":"ok","version":"1","uptimeSeconds":1,"schemaVersion":1,"indexer":"Idle"}""", Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>
    /// Прогресс без перехода в контекст синхронизации: значения видны сразу после завершения вызова.
    /// </summary>
    private sealed class SyncProgress(List<long> values) : IProgress<long>
    {
        public void Report(long value)
        {
            lock (values)
            {
                values.Add(value);
            }
        }
    }
}

using System.Net;
using System.Net.Http.Json;

using DiskApp.Contracts;
using DiskApp.Core.Indexing;
using DiskApp.Core.Search;
using DiskApp.Infrastructure;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DiskApp.Server.Tests.Hosting;

/// <summary>
/// Хост сервера (PLAN.md, этап 4.1): Kestrel на loopback с динамическим портом, открытие базы и индексатор до приёма
/// запросов, <c>/health</c>, ошибки в формате ProblemDetails, общий контейнер с оболочкой, остановка без утечек.
/// </summary>
public sealed class DiskAppServerHostTests : IDisposable
{
    private readonly TemporaryDirectory _data = new("server");

    [Fact]
    public async Task StartAsync_DefaultUrl_ListensOnLoopbackWithDynamicPort()
    {
        // Arrange
        await using var host = DiskAppServerHost.Create(Options());

        // Act
        await host.StartAsync(TestContext.Current.CancellationToken);

        // Assert
        host.BaseAddress.ShouldNotBeNull();
        host.BaseAddress.Host.ShouldBe("127.0.0.1");
        host.BaseAddress.Port.ShouldNotBe(0);
    }

    [Fact]
    public async Task Health_AfterStart_ReportsVersionSchemaAndIdleIndexer()
    {
        // Arrange
        await using var host = DiskAppServerHost.Create(Options() with { StartIndexer = true });
        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.Services.GetRequiredService<IndexerService>().WhenIdleAsync(TestContext.Current.CancellationToken);
        using var client = Client(host);

        // Act
        var response = await client.GetAsync(new Uri(ApiRoutes.Health, UriKind.Relative), TestContext.Current.CancellationToken);
        var health = await response.Content.ReadFromJsonAsync(DiskAppJsonContext.Default.HealthDto, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        health.ShouldNotBeNull();
        health.Status.ShouldBe("ok");
        health.Version.ShouldNotBeNullOrWhiteSpace();
        health.SchemaVersion.ShouldBeGreaterThanOrEqualTo(1);
        health.Indexer.ShouldBe(Contracts.Roots.IndexerState.Idle);
        File.Exists(_data.Resolve("index.db")).ShouldBeTrue();
    }

    [Fact]
    public async Task Health_Json_UsesCamelCase()
    {
        // Arrange
        await using var host = DiskAppServerHost.Create(Options());
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var client = Client(host);

        // Act
        var json = await client.GetStringAsync(new Uri(ApiRoutes.Health, UriKind.Relative), TestContext.Current.CancellationToken);

        // Assert
        json.ShouldContain("\"status\":\"ok\"");
        json.ShouldContain("\"schemaVersion\":");
        json.ShouldContain("\"uptimeSeconds\":");
    }

    [Fact]
    public async Task UnknownRoute_Returns404ProblemDetails()
    {
        // Arrange
        await using var host = DiskAppServerHost.Create(Options());
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var client = Client(host);

        // Act
        var response = await client.GetAsync(new Uri(ApiRoutes.V1.Prefix + "/missing", UriKind.Relative), TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task UnhandledException_Returns500ProblemDetailsWithoutStackTrace()
    {
        // Arrange
        await using var host = DiskAppServerHost.Create(
            Options(),
            configureApp: static app => app.MapGet("/test/throw", static string () => throw new InvalidOperationException("секретная деталь")));
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var client = Client(host);

        // Act
        var response = await client.GetAsync(new Uri("/test/throw", UriKind.Relative), TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        body.ShouldNotContain("секретная деталь");
        body.ShouldNotContain("InvalidOperationException");
    }

    [Fact]
    public async Task Services_ShellRegistration_IsSharedWithEndpointsAndCore()
    {
        // Arrange
        await using var host = DiskAppServerHost.Create(
            Options(),
            configureServices: static services => services.AddSingleton<ShellMarker>(),
            configureApp: static app => app.MapGet("/test/marker", static (ShellMarker marker, SearchService search) => marker.Id.ToString()));
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var client = Client(host);

        // Act
        var fromEndpoint = await client.GetStringAsync(new Uri("/test/marker", UriKind.Relative), TestContext.Current.CancellationToken);
        var fromHost = host.Services.GetRequiredService<ShellMarker>();

        // Assert
        fromEndpoint.ShouldBe(fromHost.Id.ToString());
        host.Services.GetRequiredService<SearchService>().ShouldBeSameAs(host.Services.GetRequiredService<SearchService>());
    }

    [Fact]
    public async Task StopAsync_AfterStart_ReleasesPortAndDatabaseFile()
    {
        // Arrange
        var host = DiskAppServerHost.Create(Options() with { StartIndexer = true });
        await host.StartAsync(TestContext.Current.CancellationToken);
        var address = host.BaseAddress!;

        // Act
        await host.StopAsync(TestContext.Current.CancellationToken);
        await host.DisposeAsync();

        // Assert
        using var client = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(5) };
        await Should.ThrowAsync<HttpRequestException>(() => client.GetAsync(new Uri(ApiRoutes.Health, UriKind.Relative), TestContext.Current.CancellationToken));
        Should.NotThrow(() => File.Delete(_data.Resolve("index.db")));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _data.Dispose();
    }

    private DiskAppServerOptions Options() => new()
    {
        Paths = AppPaths.ForDataDirectory(_data.FullPath),
        StartIndexer = false,
    };

    private static HttpClient Client(DiskAppServerHost host) => new() { BaseAddress = host.BaseAddress, Timeout = TimeSpan.FromSeconds(10) };

    private sealed class ShellMarker
    {
        public Guid Id { get; } = Guid.NewGuid();
    }
}

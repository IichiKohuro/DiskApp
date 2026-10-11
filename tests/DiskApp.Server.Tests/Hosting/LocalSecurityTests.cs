using System.Net;
using System.Text;

using DiskApp.Contracts;
using DiskApp.Infrastructure;

using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DiskApp.Server.Tests.Hosting;

/// <summary>
/// Безопасность локального режима (PLAN.md, этап 4.6): только loopback-адрес и заголовок Host, запрет чужого Origin,
/// токен сессии за флагом (заголовок и параметр хаба), лимит размера тела; токен и текст запросов не попадают в журнал (этап 10.4).
/// </summary>
public sealed class LocalSecurityTests : IDisposable
{
    private readonly TemporaryDirectory _data = new("security");

    [Fact]
    public async Task ForeignOrigin_IsRejectedAndSameOriginAllowed()
    {
        // Arrange
        await using var host = await StartAsync(Options());
        using var client = Client(host);

        // Act
        using var foreign = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiRoutes.V1.IndexRebuild, UriKind.Relative));
        foreign.Headers.Add("Origin", "https://evil.example");
        using var foreignResponse = await client.SendAsync(foreign, TestContext.Current.CancellationToken);
        using var same = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiRoutes.V1.Roots, UriKind.Relative));
        same.Headers.Add("Origin", host.BaseAddress!.GetLeftPart(UriPartial.Authority));
        using var sameResponse = await client.SendAsync(same, TestContext.Current.CancellationToken);

        // Assert
        foreignResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        foreignResponse.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        sameResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ForeignHostHeader_IsRejected()
    {
        // Arrange
        await using var host = await StartAsync(Options());
        using var client = Client(host);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiRoutes.V1.Roots, UriKind.Relative));
        request.Headers.Host = "rebind.evil.example";

        // Act
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SessionToken_RequiredForApiAndHubButNotHealth()
    {
        // Arrange
        var token = DiskAppServerOptions.CreateSessionToken();
        await using var host = await StartAsync(Options() with { SessionToken = token });
        using var client = Client(host);

        // Act
        using var missing = await client.GetAsync(new Uri(ApiRoutes.V1.Roots, UriKind.Relative), TestContext.Current.CancellationToken);
        using var wrongRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiRoutes.V1.Roots, UriKind.Relative));
        wrongRequest.Headers.Add(ApiHeaders.SessionToken, "not-the-token");
        using var wrong = await client.SendAsync(wrongRequest, TestContext.Current.CancellationToken);
        using var rightRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiRoutes.V1.Roots, UriKind.Relative));
        rightRequest.Headers.Add(ApiHeaders.SessionToken, token);
        using var right = await client.SendAsync(rightRequest, TestContext.Current.CancellationToken);
        using var health = await client.GetAsync(new Uri(ApiRoutes.Health, UriKind.Relative), TestContext.Current.CancellationToken);

        await using var anonymousHub = HubConnection(host, token: null);
        var anonymousFailed = await Should.ThrowAsync<HttpRequestException>(() => anonymousHub.StartAsync(TestContext.Current.CancellationToken));
        await using var hub = HubConnection(host, token);
        await hub.StartAsync(TestContext.Current.CancellationToken);

        // Assert
        missing.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        right.StatusCode.ShouldBe(HttpStatusCode.OK);
        health.StatusCode.ShouldBe(HttpStatusCode.OK);
        anonymousFailed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        hub.State.ShouldBe(HubConnectionState.Connected);
    }

    [Fact]
    public async Task Logs_NeverContainSessionTokenOrQueryText()
    {
        // Arrange: свои записи — на самом подробном уровне, записи ASP.NET Core — от предупреждений, как в журнале
        // приложения (Program.CreateLogger)
        var token = DiskAppServerOptions.CreateSessionToken();
        var captured = new CapturingLoggerProvider();
        var host = DiskAppServerHost.Create(Options() with { SessionToken = token }, services => services.AddLogging(logging => logging
            .SetMinimumLevel(LogLevel.Trace)
            .AddFilter("Microsoft", LogLevel.Warning)
            .AddProvider(captured)));
        await host.StartAsync(TestContext.Current.CancellationToken);
        await using var _ = host;
        using var client = Client(host);
        const string Query = "паспорт-серия-4510";

        // Act
        using var search = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiRoutes.V1.Search + "?q=" + Uri.EscapeDataString(Query), UriKind.Relative));
        search.Headers.Add(ApiHeaders.SessionToken, token);
        using var searched = await client.SendAsync(search, TestContext.Current.CancellationToken);
        using var wrong = new HttpRequestMessage(HttpMethod.Get, new Uri(ApiRoutes.V1.Roots, UriKind.Relative));
        wrong.Headers.Add(ApiHeaders.SessionToken, token + "x");
        using var rejected = await client.SendAsync(wrong, TestContext.Current.CancellationToken);
        using var negotiated = await client.PostAsync(
            new Uri(HubRoutes.Path + "/negotiate?negotiateVersion=1&" + ApiHeaders.SessionTokenQuery + "=" + Uri.EscapeDataString(token), UriKind.Relative),
            null,
            TestContext.Current.CancellationToken);

        // Assert
        searched.StatusCode.ShouldBe(HttpStatusCode.OK);
        rejected.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        negotiated.StatusCode.ShouldBe(HttpStatusCode.OK);
        captured.Messages.ShouldNotBeEmpty();
        captured.Messages.ShouldAllBe(message => !message.Contains(token, StringComparison.Ordinal) && !message.Contains(Query, StringComparison.Ordinal));
    }

    [Fact]
    public void ToString_EndpointAndOptions_HideSessionToken()
    {
        // Arrange
        var token = DiskAppServerOptions.CreateSessionToken();

        // Act
        var endpoint = new DiskApp.Client.ServerEndpoint(new Uri("http://127.0.0.1:5000/"), token).ToString();
        var options = (Options() with { SessionToken = token }).ToString();

        // Assert
        endpoint.ShouldNotContain(token);
        endpoint.ShouldContain("http://127.0.0.1:5000/");
        options.ShouldNotContain(token);
        options.ShouldContain(DiskAppServerOptions.LoopbackDynamicUrl);
    }

    [Fact]
    public async Task BodyOverLimit_Returns413()
    {
        // Arrange
        await using var host = await StartAsync(Options() with { MaxRequestBodyBytes = 1024 });
        using var client = Client(host);
        var json = "{\"search\":{\"pageSize\":50},\"padding\":\"" + new string('x', 4096) + "\"}";

        // Act
        using var response = await client.PatchAsync(new Uri(ApiRoutes.V1.Settings, UriKind.Relative), new StringContent(json, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Theory]
    [InlineData("http://0.0.0.0:0")]
    [InlineData("http://192.168.1.10:5000")]
    [InlineData("http://*:5000")]
    public void Create_NonLoopbackUrl_Throws(string url)
        => Should.Throw<ArgumentException>(() => DiskAppServerHost.Create(Options() with { Url = url }));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _data.Dispose();
    }

    private DiskAppServerOptions Options() => new() { Paths = AppPaths.ForDataDirectory(_data.FullPath), StartIndexer = false };

    private static async Task<DiskAppServerHost> StartAsync(DiskAppServerOptions options)
    {
        var host = DiskAppServerHost.Create(options);
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private static HttpClient Client(DiskAppServerHost host) => new() { BaseAddress = host.BaseAddress, Timeout = TimeSpan.FromSeconds(10) };

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => owner._messages.Enqueue($"{category}: {formatter(state, exception)} {exception}");
        }
    }

    private static HubConnection HubConnection(DiskAppServerHost host, string? token)
        => new HubConnectionBuilder()
            .WithUrl(new Uri(host.BaseAddress!, HubRoutes.Path), http =>
            {
                if (token is not null)
                {
                    http.Headers[ApiHeaders.SessionToken] = token;
                }
            })
            .AddJsonProtocol(static json => json.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, DiskAppJsonContext.Default))
            .Build();
}

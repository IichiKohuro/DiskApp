using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace DiskApp.Server.Tests.Hosting;

/// <summary>
/// Дымовая проверка схемы из решения 1 PLAN.md: Kestrel поднимается внутри процесса на <c>127.0.0.1</c>
/// с динамическим портом, фактический адрес известен после старта, запрос по loopback проходит.
/// </summary>
public sealed class LoopbackHostingTests
{
    [Fact]
    public async Task MinimalApi_OnLoopbackDynamicPort_RespondsToGet()
    {
        // Arrange
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        await using var app = builder.Build();
        app.MapGet("/ping", static () => "pong");

        // Act
        await app.StartAsync(TestContext.Current.CancellationToken);
        var address = app.Urls.Single();

        using var client = new HttpClient { BaseAddress = new Uri(address) };
        var response = await client.GetStringAsync(new Uri("/ping", UriKind.Relative), TestContext.Current.CancellationToken);

        await app.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        var uri = new Uri(address);
        uri.Host.ShouldBe("127.0.0.1");
        uri.Port.ShouldNotBe(0);
        response.ShouldBe("pong");
    }
}

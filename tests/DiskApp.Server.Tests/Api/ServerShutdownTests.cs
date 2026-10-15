using System.Diagnostics;

using DiskApp.Client;

namespace DiskApp.Server.Tests.Api;

/// <summary>
/// Выход из приложения (FEEDBACK.md, 2026-10-14: остановка иногда ждала ровно 5 с): сервер с корнем и подключённым хабом
/// приложения — оно переподключается сразу — останавливается быстро, не дожидаясь таймаута оболочки.
/// </summary>
public sealed class ServerShutdownTests
{
    [Fact]
    public async Task Stop_WithRootAndAppHubConnected_FinishesWellBeforeTimeout()
    {
        // Arrange
        var context = await ServerTestContext.StartAsync();
        context.Files.CreateFile("a.txt", "a");
        await context.AddRootAsync();
        await using var hub = new DiskAppHubConnection(new FixedServerEndpointProvider(new ServerEndpoint(context.Host.BaseAddress!)));
        await hub.StartAsync(TestContext.Current.CancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stopwatch = Stopwatch.StartNew();

        // Act
        await context.Host.StopAsync(timeout.Token);
        stopwatch.Stop();
        await context.DisposeAsync();

        // Assert
        timeout.IsCancellationRequested.ShouldBeFalse();
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
    }
}

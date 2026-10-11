using Avalonia.Controls.ApplicationLifetimes;

using DiskApp.Desktop.Shell;
using DiskApp.Desktop.SingleInstance;

using Microsoft.Extensions.DependencyInjection;

using Serilog;

namespace DiskApp.Desktop.Tests.Shell;

/// <summary>
/// Выход (PLAN.md, этап 9.5): хост освобождает контейнер раньше оболочки, и её <c>Dispose</c> не должен обращаться к
/// нему — иначе исключение роняло процесс при каждом выходе и оставляло отметку аварийного завершения.
/// </summary>
public sealed class DesktopShellDisposeTests
{
    [Fact]
    public async Task Dispose_AfterContainerDisposed_DoesNotThrow()
    {
        // Arrange
        var provider = new ServiceCollection().BuildServiceProvider();
        var shell = new DesktopShell(
            Substitute.For<IClassicDesktopStyleApplicationLifetime>(),
            provider,
            InstanceNames.ForCurrentUser("DiskApp.Tests.Dispose"),
            new LoggerConfiguration().CreateLogger());
        await provider.DisposeAsync();

        // Act & Assert
        Should.NotThrow(shell.Dispose);
    }
}

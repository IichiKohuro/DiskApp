using DiskApp.Desktop.SingleInstance;

using Serilog;

namespace DiskApp.Desktop.Tests.SingleInstance;

/// <summary>
/// Одиночный экземпляр: мьютекс отдаёт первенство одному процессу, второй передаёт аргументы по каналу.
/// </summary>
public sealed class SingleInstanceTests
{
    [Fact]
    public void Acquire_FromAnotherThreadWhileHeld_ReportsNotFirst()
    {
        // Arrange
        var name = @"Local\DiskApp.Tests." + Guid.NewGuid().ToString("N");
        using var first = SingleInstanceGuard.Acquire(name);

        // Act: мьютекс реентерабелен для своего потока, поэтому «второй экземпляр» изображает отдельный поток
        // (не из пула: пул может вернуть тот же поток, что владеет мьютексом).
        var secondIsFirst = true;
        var thread = new Thread(() =>
        {
            using var second = SingleInstanceGuard.Acquire(name);
            secondIsFirst = second.IsFirstInstance;
        });
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(5)).ShouldBeTrue();

        // Assert
        first.IsFirstInstance.ShouldBeTrue();
        secondIsFirst.ShouldBeFalse();
    }

    [Fact]
    public void Acquire_AfterFirstReleased_BecomesFirstAgain()
    {
        // Arrange
        var name = @"Local\DiskApp.Tests." + Guid.NewGuid().ToString("N");
        var first = SingleInstanceGuard.Acquire(name);

        // Act
        first.Dispose();
        using var next = SingleInstanceGuard.Acquire(name);

        // Assert
        next.IsFirstInstance.ShouldBeTrue();
    }

    [Fact]
    public async Task Server_ReceivesArgumentsSentByClient()
    {
        // Arrange
        var pipeName = "DiskApp.Tests." + Guid.NewGuid().ToString("N");
        var received = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new InstanceServer(pipeName, args => received.TrySetResult(args), Log.Logger);
        server.Start();
        string[] args = ["--search", "счета сентябрь"];

        // Act
        var sent = await InstanceClient.TrySendAsync(pipeName, args, TimeSpan.FromSeconds(5));
        var result = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        sent.ShouldBeTrue();
        result.ShouldBe(args);
    }

    [Fact]
    public async Task Server_AfterOneClient_AcceptsTheNext()
    {
        // Arrange
        var pipeName = "DiskApp.Tests." + Guid.NewGuid().ToString("N");
        var messages = new List<IReadOnlyList<string>>();
        var secondReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new InstanceServer(pipeName, args =>
        {
            lock (messages)
            {
                messages.Add(args);
                if (messages.Count == 2)
                {
                    secondReceived.TrySetResult();
                }
            }
        }, Log.Logger);
        server.Start();

        // Act
        (await InstanceClient.TrySendAsync(pipeName, ["--show"], TimeSpan.FromSeconds(5))).ShouldBeTrue();
        (await InstanceClient.TrySendAsync(pipeName, ["--settings"], TimeSpan.FromSeconds(5))).ShouldBeTrue();
        await secondReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert
        lock (messages)
        {
            messages.Select(static m => m[0]).ShouldBe(["--show", "--settings"]);
        }
    }

    [Fact]
    public async Task TrySendAsync_WithoutServer_ReturnsFalseWithinTimeout()
    {
        // Arrange
        var pipeName = "DiskApp.Tests.Missing." + Guid.NewGuid().ToString("N");

        // Act
        var sent = await InstanceClient.TrySendAsync(pipeName, ["--show"], TimeSpan.FromMilliseconds(300));

        // Assert
        sent.ShouldBeFalse();
    }

    [Fact]
    public void ForCurrentUser_Always_ProducesSessionScopedNames()
    {
        // Act
        var names = InstanceNames.ForCurrentUser();

        // Assert
        names.Mutex.ShouldStartWith(@"Local\DiskApp.");
        names.Pipe.ShouldStartWith("DiskApp.");
        names.Pipe.ShouldNotContain(" ");
        names.Pipe.ShouldNotContain("\\");
    }

    [Fact]
    public void ForCurrentUser_PortableDataScope_SeparatesCopiesByDataFolder()
    {
        // Act
        var installed = InstanceNames.ForCurrentUser();
        var portable = InstanceNames.ForCurrentUser(dataScope: @"D:\Tools\DiskApp\data");
        var samePortable = InstanceNames.ForCurrentUser(dataScope: @"d:\tools\diskapp\data\");
        var otherPortable = InstanceNames.ForCurrentUser(dataScope: @"E:\DiskApp\data");

        // Assert
        portable.ShouldNotBe(installed);
        portable.ShouldBe(samePortable);
        portable.ShouldNotBe(otherPortable);
        portable.Mutex.ShouldStartWith(installed.Mutex[..^".Instance".Length]);
    }
}

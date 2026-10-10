using DiskApp.Core.Indexing;

namespace DiskApp.Core.Tests.Indexing;

/// <summary>
/// Пауза задач индексации: ожидание при паузе, снятие отпускает ждущих, отмена прерывает ожидание, токен по умолчанию.
/// </summary>
public sealed class PauseTokenTests
{
    [Fact]
    public async Task WaitWhilePausedAsync_NotPaused_CompletesImmediately()
    {
        // Arrange
        var source = new PauseTokenSource();

        // Act
        var task = source.Token.WaitWhilePausedAsync(TestContext.Current.CancellationToken);

        // Assert
        task.IsCompleted.ShouldBeTrue();
        await task;
        PauseToken.None.IsPaused.ShouldBeFalse();
        await PauseToken.None.WaitWhilePausedAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Resume_AfterPause_ReleasesAsyncAndSyncWaiters()
    {
        // Arrange
        var source = new PauseTokenSource();
        source.Pause();
        source.Pause();
        var asyncWaiter = source.Token.WaitWhilePausedAsync(TestContext.Current.CancellationToken);
        var syncWaiter = Task.Factory.StartNew(
            () => source.Token.WaitWhilePaused(TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        // Act
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var pendingBeforeResume = !asyncWaiter.IsCompleted && !syncWaiter.IsCompleted;
        source.Resume();
        source.Resume();

        // Assert
        pendingBeforeResume.ShouldBeTrue();
        source.Token.IsPaused.ShouldBeFalse();
        await asyncWaiter.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await syncWaiter.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WaitWhilePausedAsync_Cancelled_Throws()
    {
        // Arrange
        var source = new PauseTokenSource();
        source.Pause();
        using var cancellation = new CancellationTokenSource();

        // Act
        var waiter = source.Token.WaitWhilePausedAsync(cancellation.Token);
        await cancellation.CancelAsync();

        // Assert
        await Should.ThrowAsync<OperationCanceledException>(waiter);
        source.IsPaused.ShouldBeTrue();
    }

    [Fact]
    public void Equality_SameSource_Equal()
    {
        // Arrange
        var source = new PauseTokenSource();

        // Act & Assert
        (source.Token == source.Token).ShouldBeTrue();
        (source.Token != PauseToken.None).ShouldBeTrue();
        source.Token.Equals((object)source.Token).ShouldBeTrue();
        source.Token.GetHashCode().ShouldBe(source.Token.GetHashCode());
    }
}

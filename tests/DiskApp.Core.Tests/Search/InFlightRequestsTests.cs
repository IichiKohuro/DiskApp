using DiskApp.Core.Search;

namespace DiskApp.Core.Tests.Search;

/// <summary>
/// Объединение одновременных запросов (PLAN.md, этап 10.2): одинаковые идут в базу один раз, отмена — только когда
/// отказались все, готовый результат не кэшируется.
/// </summary>
public sealed class InFlightRequestsTests
{
    [Fact]
    public async Task SameKey_RunsWorkOnceForAllWaiters()
    {
        // Arrange
        var requests = new InFlightRequests<int>();
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        Task<int> Work(CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            return gate.Task;
        }

        // Act
        var first = requests.RunAsync("q", Work, TestContext.Current.CancellationToken);
        var second = requests.RunAsync("q", Work, TestContext.Current.CancellationToken);
        var other = requests.RunAsync("other", Work, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => Volatile.Read(ref calls) == 2);
        var running = requests.Count;
        gate.SetResult(42);

        // Assert
        (await first).ShouldBe(42);
        (await second).ShouldBe(42);
        (await other).ShouldBe(42);
        calls.ShouldBe(2, "«q» — один раз, «other» — один раз");
        running.ShouldBe(2);
        requests.Count.ShouldBe(0, "готовый результат не хранится");
    }

    [Fact]
    public async Task OneWaiterCancels_WorkContinuesForTheOther()
    {
        // Arrange
        var requests = new InFlightRequests<int>();
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workToken = CancellationToken.None;
        using var leaving = new CancellationTokenSource();

        // Act
        var left = requests.RunAsync("q", token => { workToken = token; return gate.Task; }, leaving.Token);
        var stays = requests.RunAsync("q", _ => gate.Task, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => workToken.CanBeCanceled);
        await leaving.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => left);
        var cancelledAfterOne = workToken.IsCancellationRequested;
        gate.SetResult(7);

        // Assert
        cancelledAfterOne.ShouldBeFalse("второй ещё ждёт");
        (await stays).ShouldBe(7);
    }

    [Fact]
    public async Task AllWaitersCancel_WorkIsCancelled()
    {
        // Arrange
        var requests = new InFlightRequests<int>();
        var workToken = CancellationToken.None;
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        static async Task<int> Forever(CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }

        // Act
        var a = requests.RunAsync("q", token => { workToken = token; return Forever(token); }, first.Token);
        var b = requests.RunAsync("q", Forever, second.Token);
        await WaitUntilAsync(() => workToken.CanBeCanceled);
        await first.CancelAsync();
        await second.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => a);
        await Should.ThrowAsync<OperationCanceledException>(() => b);

        // Assert
        workToken.IsCancellationRequested.ShouldBeTrue("запрос никому не нужен — база его прерывает");
        await WaitUntilAsync(() => requests.Count == 0);
    }

    [Fact]
    public async Task AfterCompletion_NextCallRunsAgain_AndErrorsReachEveryone()
    {
        // Arrange
        var requests = new InFlightRequests<int>();
        var calls = 0;

        // Act
        var one = await requests.RunAsync("q", _ => Task.FromResult(Interlocked.Increment(ref calls)), TestContext.Current.CancellationToken);
        var two = await requests.RunAsync("q", _ => Task.FromResult(Interlocked.Increment(ref calls)), TestContext.Current.CancellationToken);
        var failure = requests.RunAsync("bad", _ => Task.FromException<int>(new InvalidOperationException("сломалось")), TestContext.Current.CancellationToken);

        // Assert
        one.ShouldBe(1);
        two.ShouldBe(2);
        (await Should.ThrowAsync<InvalidOperationException>(() => failure)).Message.ShouldBe("сломалось");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        condition().ShouldBeTrue();
    }
}

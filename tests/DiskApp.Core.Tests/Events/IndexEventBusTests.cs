using DiskApp.Core.Domain;
using DiskApp.Core.Events;

namespace DiskApp.Core.Tests.Events;

/// <summary>
/// Шина событий в памяти: доставка по типу, порядок подписки, отписка, изоляция ошибок подписчиков, потокобезопасность.
/// </summary>
public sealed class IndexEventBusTests
{
    [Fact]
    public void Publish_SubscribedType_DeliversInSubscriptionOrder()
    {
        // Arrange
        var bus = new IndexEventBus();
        var received = new List<string>();
        using var first = bus.Subscribe<RootStatusChangedEvent>(e => received.Add("first:" + e.Status));
        using var second = bus.Subscribe<RootStatusChangedEvent>(e => received.Add("second:" + e.Status));
        using var other = bus.Subscribe<IndexProgressEvent>(_ => received.Add("progress"));

        // Act
        bus.Publish(new RootStatusChangedEvent(1, RootStatus.Scanning, null));

        // Assert
        received.ShouldBe(["first:Scanning", "second:Scanning"]);
    }

    [Fact]
    public void Publish_WithoutSubscribers_DoesNothing()
    {
        // Arrange
        var bus = new IndexEventBus();

        // Act & Assert
        Should.NotThrow(() => bus.Publish(new OperationCompletedEvent(Guid.NewGuid(), OperationOutcome.Completed, null)));
    }

    [Fact]
    public void Dispose_Subscription_StopsDeliveryAndIsIdempotent()
    {
        // Arrange
        var bus = new IndexEventBus();
        var count = 0;
        var subscription = bus.Subscribe<RootStatusChangedEvent>(_ => count++);

        // Act
        subscription.Dispose();
        subscription.Dispose();
        bus.Publish(new RootStatusChangedEvent(1, RootStatus.Online, null));

        // Assert
        count.ShouldBe(0);
        bus.SubscriberCount<RootStatusChangedEvent>().ShouldBe(0);
    }

    [Fact]
    public void Publish_HandlerThrows_OthersStillReceiveAndErrorIsReported()
    {
        // Arrange
        var bus = new IndexEventBus();
        var errors = new List<(Exception Exception, IIndexEvent Event)>();
        bus.OnHandlerError = (exception, indexEvent) => errors.Add((exception, indexEvent));
        var delivered = false;
        using var failing = bus.Subscribe<RootStatusChangedEvent>(_ => throw new InvalidOperationException("boom"));
        using var healthy = bus.Subscribe<RootStatusChangedEvent>(_ => delivered = true);
        var indexEvent = new RootStatusChangedEvent(2, RootStatus.Error, "диск извлечён");

        // Act
        bus.Publish(indexEvent);

        // Assert
        delivered.ShouldBeTrue();
        errors.ShouldHaveSingleItem();
        errors[0].Exception.Message.ShouldBe("boom");
        errors[0].Event.ShouldBeSameAs(indexEvent);
    }

    [Fact]
    public void Subscribe_DuringPublish_DoesNotAffectCurrentDelivery()
    {
        // Arrange
        var bus = new IndexEventBus();
        var lateCalls = 0;
        IDisposable? late = null;
        using var subscription = bus.Subscribe<IndexProgressEvent>(_ => late ??= bus.Subscribe<IndexProgressEvent>(_ => lateCalls++));
        var progress = new IndexProgressEvent(1, IndexPhase.Enumerating, 10, 0, @"D:\Projects", 1000, TimeSpan.FromSeconds(1));

        // Act
        bus.Publish(progress);
        bus.Publish(progress);

        // Assert
        lateCalls.ShouldBe(1, "подписчик, добавленный во время доставки, получает только следующее событие");
        late?.Dispose();
    }

    [Fact]
    public async Task PublishAndSubscribe_FromManyThreads_DoNotLoseOrDuplicateEvents()
    {
        // Arrange
        var bus = new IndexEventBus();
        var total = 0;
        using var subscription = bus.Subscribe<OperationCompletedEvent>(_ => Interlocked.Increment(ref total));
        const int threads = 8;
        const int perThread = 500;

        // Act
        await Task.WhenAll(Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < perThread; i++)
            {
                using var transient = bus.Subscribe<RootStatusChangedEvent>(_ => { });
                bus.Publish(new OperationCompletedEvent(Guid.NewGuid(), OperationOutcome.Completed, null));
            }
        }, TestContext.Current.CancellationToken)));

        // Assert
        total.ShouldBe(threads * perThread);
        bus.SubscriberCount<RootStatusChangedEvent>().ShouldBe(0);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 0.0)]
    [InlineData(5, 10, 0, 0, 0.5)]
    [InlineData(5, 10, 300, 400, 0.75)]
    [InlineData(9, 10, 500, 400, 1.0)]
    public void OperationProgress_Fraction_PrefersBytesOverItems(long items, long totalItems, long bytes, long totalBytes, double expected)
    {
        // Act
        var fraction = new OperationProgressEvent(Guid.Empty, items, totalItems, bytes, totalBytes, null).Fraction;

        // Assert
        fraction.ShouldBe(expected, tolerance: 1e-9);
    }
}

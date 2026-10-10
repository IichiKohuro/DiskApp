using System.Collections.Immutable;

namespace DiskApp.Core.Events;

/// <summary>
/// Шина событий в памяти: подписчики хранятся в неизменяемых списках по типу события, поэтому публикация
/// не блокируется подпиской, а отписка во время доставки безопасна. Исключение подписчика передаётся
/// в <see cref="OnHandlerError"/> и не мешает остальным.
/// </summary>
public sealed class IndexEventBus : IIndexEventBus
{
    private readonly object _gate = new();
    private readonly Dictionary<Type, ImmutableArray<Subscription>> _subscriptions = [];

    /// <summary>
    /// Обработчик ошибок подписчиков; по умолчанию ошибки проглатываются. Оболочка подключает сюда журнал.
    /// </summary>
    public Action<Exception, IIndexEvent>? OnHandlerError { get; set; }

    /// <inheritdoc />
    public void Publish<TEvent>(TEvent indexEvent)
        where TEvent : class, IIndexEvent
    {
        ArgumentNullException.ThrowIfNull(indexEvent);

        ImmutableArray<Subscription> handlers;
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(typeof(TEvent), out handlers))
            {
                return;
            }
        }

        foreach (var subscription in handlers)
        {
            try
            {
                ((Action<TEvent>)subscription.Handler)(indexEvent);
            }
            catch (Exception exception)
            {
                OnHandlerError?.Invoke(exception, indexEvent);
            }
        }
    }

    /// <inheritdoc />
    public IDisposable Subscribe<TEvent>(Action<TEvent> handler)
        where TEvent : class, IIndexEvent
    {
        ArgumentNullException.ThrowIfNull(handler);

        var subscription = new Subscription(this, typeof(TEvent), handler);
        lock (_gate)
        {
            var existing = _subscriptions.TryGetValue(typeof(TEvent), out var handlers) ? handlers : [];
            _subscriptions[typeof(TEvent)] = existing.Add(subscription);
        }

        return subscription;
    }

    /// <summary>
    /// Число подписчиков на тип события; для тестов и диагностики.
    /// </summary>
    public int SubscriberCount<TEvent>()
        where TEvent : class, IIndexEvent
    {
        lock (_gate)
        {
            return _subscriptions.TryGetValue(typeof(TEvent), out var handlers) ? handlers.Length : 0;
        }
    }

    private void Unsubscribe(Subscription subscription)
    {
        lock (_gate)
        {
            if (_subscriptions.TryGetValue(subscription.EventType, out var handlers))
            {
                var remaining = handlers.Remove(subscription);
                if (remaining.IsEmpty)
                {
                    _subscriptions.Remove(subscription.EventType);
                }
                else
                {
                    _subscriptions[subscription.EventType] = remaining;
                }
            }
        }
    }

    private sealed class Subscription(IndexEventBus bus, Type eventType, Delegate handler) : IDisposable
    {
        private int _disposed;

        public Type EventType { get; } = eventType;

        public Delegate Handler { get; } = handler;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                bus.Unsubscribe(this);
            }
        }
    }
}

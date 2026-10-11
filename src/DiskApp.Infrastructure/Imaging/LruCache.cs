namespace DiskApp.Infrastructure.Imaging;

/// <summary>
/// Потокобезопасный кэш с вытеснением давно не использованных записей; хранит и <c>null</c> — «изображения нет».
/// </summary>
/// <typeparam name="TKey">Ключ.</typeparam>
/// <typeparam name="TValue">Значение.</typeparam>
public sealed class LruCache<TKey, TValue>
    where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map = [];
    private readonly LinkedList<(TKey Key, TValue Value)> _order = new();
    private readonly Lock _gate = new();

    /// <summary>
    /// Создаёт кэш на <paramref name="capacity"/> записей.
    /// </summary>
    public LruCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    /// <summary>Сколько записей в кэше.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _map.Count;
            }
        }
    }

    /// <summary>
    /// Ищет запись и делает её самой свежей.
    /// </summary>
    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }

        value = default!;
        return false;
    }

    /// <summary>
    /// Добавляет или заменяет запись; при переполнении вытесняет самую старую.
    /// </summary>
    public void Set(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
            }

            _map[key] = _order.AddFirst((key, value));
            while (_map.Count > _capacity && _order.Last is { } oldest)
            {
                _order.RemoveLast();
                _map.Remove(oldest.Value.Key);
            }
        }
    }
}

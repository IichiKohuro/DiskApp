namespace DiskApp.UI.Services;

/// <summary>
/// Потокобезопасный кэш с вытеснением давно не использованных записей; хранит и <c>null</c> — «изображения нет».
/// Копия кэша инфраструктуры: интерфейс зависит только от клиента API.
/// </summary>
/// <typeparam name="TKey">Ключ.</typeparam>
/// <typeparam name="TValue">Значение.</typeparam>
internal sealed class LruCache<TKey, TValue>
    where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map = [];
    private readonly LinkedList<(TKey Key, TValue Value)> _order = new();
    private readonly Lock _gate = new();

    public LruCache(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

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

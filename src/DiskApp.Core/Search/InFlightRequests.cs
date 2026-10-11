namespace DiskApp.Core.Search;

/// <summary>
/// Объединяет одинаковые запросы, которые выполняются одновременно (PLAN.md, этап 10.2): второй и следующие с тем же
/// ключом не идут в базу, а ждут результата первого. Работа отменяется, только когда отказались все ожидающие, — поэтому
/// при наборе текста устаревший запрос по-прежнему прерывается. Готовый результат не кэшируется: следующий запрос после
/// завершения выполняется заново.
/// </summary>
/// <typeparam name="T">Результат.</typeparam>
public sealed class InFlightRequests<T>
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Сколько запросов выполняется сейчас.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Выполняет работу или присоединяется к уже идущей с тем же ключом.</summary>
    /// <param name="key">Ключ: одинаковый у запросов с одинаковым результатом.</param>
    /// <param name="work">Работа; получает токен, который отменяется, когда отказались все ожидающие.</param>
    /// <param name="cancellationToken">Отмена этого ожидающего.</param>
    public async Task<T> RunAsync(string key, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(work);
        cancellationToken.ThrowIfCancellationRequested();

        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var existing) || existing.Cancellation.IsCancellationRequested)
            {
                // Запись попадает в словарь раньше запуска: завершение снимает её под той же блокировкой.
                existing = new Entry();
                _entries[key] = existing;
                var started = existing;
                existing.Task = Task.Run(() => ExecuteAsync(key, started, work), CancellationToken.None);
            }

            entry = existing;
            entry.Waiters++;
        }

        try
        {
            return await entry.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (--entry.Waiters == 0 && !entry.Task.IsCompleted)
                {
                    entry.Cancellation.Cancel();
                }
            }
        }
    }

    private async Task<T> ExecuteAsync(string key, Entry entry, Func<CancellationToken, Task<T>> work)
    {
        try
        {
            return await work(entry.Cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                {
                    _entries.Remove(key);
                }
            }
        }
    }

    private sealed class Entry
    {
        public CancellationTokenSource Cancellation { get; } = new();

        public int Waiters { get; set; }

        public Task<T> Task { get; set; } = null!;
    }
}

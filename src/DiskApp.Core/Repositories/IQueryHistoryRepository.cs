namespace DiskApp.Core.Repositories;

/// <summary>
/// История поисковых запросов для подсказок в окне поиска.
/// </summary>
public interface IQueryHistoryRepository
{
    /// <summary>Запоминает выполненный запрос: новый добавляется, существующий получает +1 и свежее время.</summary>
    Task RecordAsync(string query, DateTimeOffset usedAtUtc, CancellationToken cancellationToken = default);

    /// <summary>Недавние запросы, свежие первыми.</summary>
    Task<IReadOnlyList<QueryHistoryEntry>> GetRecentAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>Запросы, начинающиеся с префикса, по убыванию свежести.</summary>
    Task<IReadOnlyList<QueryHistoryEntry>> SuggestAsync(string prefix, int limit, CancellationToken cancellationToken = default);

    /// <summary>Удаляет один запрос из истории; возвращает <c>false</c>, если его не было.</summary>
    Task<bool> RemoveAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>Очищает историю.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Запись истории запросов.
/// </summary>
/// <param name="Query">Текст запроса как ввёл пользователь.</param>
/// <param name="UseCount">Сколько раз выполнялся.</param>
/// <param name="LastUsedUtc">Когда выполнялся последний раз.</param>
public sealed record QueryHistoryEntry(string Query, int UseCount, DateTimeOffset LastUsedUtc);

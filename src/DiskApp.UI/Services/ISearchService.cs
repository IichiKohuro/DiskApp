using DiskApp.UI.Models;

namespace DiskApp.UI.Services;

/// <summary>
/// Источник данных для поиска и главной страницы: <see cref="ApiSearchService"/> поверх клиента API в приложении,
/// <see cref="SampleSearchService"/> с макетными данными в превью и тестах.
/// </summary>
public interface ISearchService
{
    /// <summary>
    /// Ищет записи по строке запроса. Пустой запрос даёт пустой результат: для него есть <see cref="GetRecentAsync"/>.
    /// </summary>
    Task<SearchResult> SearchAsync(string query, CancellationToken cancellationToken);

    /// <summary>
    /// Недавно открытые записи, самые свежие первыми.
    /// </summary>
    Task<IReadOnlyList<FileEntry>> GetRecentAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Подборка для пустого запроса: секции «Недавно открытые», «Закреплённые», «Недавно изменённые» (пустые секции
    /// пропускаются). Одна запись может встретиться в нескольких секциях; повторы убирает тот, кто показывает.
    /// </summary>
    Task<IReadOnlyList<ResultSection>> GetSuggestionsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// История запросов, из которых открывали файлы, самые свежие первыми.
    /// </summary>
    Task<IReadOnlyList<string>> GetHistoryAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Файлы, изменённые за последний <paramref name="period"/>, самые свежие первыми.
    /// </summary>
    Task<IReadOnlyList<FileEntry>> GetChangedSinceAsync(TimeSpan period, CancellationToken cancellationToken);

    /// <summary>
    /// Подсказки к запросу: для пустого — запросы из истории, для фильтра в конце (<c>ext:p</c>) — его ключи и значения.
    /// Текст передаётся как есть, с завершающим пробелом: после пробела дополнять нечего.
    /// </summary>
    Task<IReadOnlyList<QuerySuggestion>> SuggestAsync(string query, CancellationToken cancellationToken);

    /// <summary>Удаляет запрос из истории.</summary>
    Task DeleteHistoryAsync(string query, CancellationToken cancellationToken);

    /// <summary>Очищает историю запросов.</summary>
    Task ClearHistoryAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Состояние индекса.
    /// </summary>
    Task<IndexStatus> GetIndexStatusAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Открывает запись приложением по умолчанию и отмечает открытие: запись поднимается в недавних и в ранжировании,
    /// а запрос, из которого её открыли, попадает в историю.
    /// </summary>
    /// <param name="entry">Запись.</param>
    /// <param name="query">Запрос, из которого открыли; <c>null</c> — открыли не из поиска.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task OpenAsync(FileEntry entry, string? query, CancellationToken cancellationToken);
}

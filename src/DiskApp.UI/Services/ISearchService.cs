using DiskApp.UI.Models;

namespace DiskApp.UI.Services;

/// <summary>
/// Источник данных для поиска и главной страницы. Сейчас единственная реализация — <see cref="SampleSearchService"/>
/// с макетными данными; в этапе 5 её заменит обёртка над <c>IDiskAppApi</c> из <c>DiskApp.Client</c>,
/// а интерфейс останется прежним.
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

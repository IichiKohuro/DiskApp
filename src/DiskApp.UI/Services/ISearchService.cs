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
    /// Отмечает, что запись открыта пользователем: она поднимается в недавних и в ранжировании.
    /// </summary>
    Task RecordOpenedAsync(FileEntry entry, CancellationToken cancellationToken);
}

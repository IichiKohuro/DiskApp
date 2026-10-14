using DiskApp.Core.Sorting;

namespace DiskApp.Core.Repositories;

/// <summary>
/// История решений разбора (PLAN.md, этап 17.4): сколько файлов с признаком имени отправлено в папку. Строк — тысячи, служба
/// подсказок держит их в памяти и пишет только изменённые.
/// </summary>
public interface ISortingStatsRepository
{
    /// <summary>Все счётчики.</summary>
    /// <param name="cancellationToken">Отмена.</param>
    Task<IReadOnlyList<SortingStat>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Записывает счётчики и удаляет ненужные одной транзакцией.
    /// </summary>
    /// <param name="upserts">Новые и изменённые счётчики: ключ — признак и папка.</param>
    /// <param name="deletes">Ключи удаляемых счётчиков.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task SaveAsync(IReadOnlyList<SortingStat> upserts, IReadOnlyList<(string Feature, string Destination)> deletes, CancellationToken cancellationToken = default);
}

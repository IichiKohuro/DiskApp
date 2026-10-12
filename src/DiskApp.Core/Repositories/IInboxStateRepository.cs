using DiskApp.Core.Sorting;

namespace DiskApp.Core.Repositories;

/// <summary>
/// Состояния файлов в папках-накопителях (PLAN.md, этап 17.1): «отложен» и «оставить здесь». Хранятся по полному пути:
/// так они переживают пересборку индекса; у нового файла записи нет — он «новый».
/// </summary>
public interface IInboxStateRepository
{
    /// <summary>Все записи: путь и состояние.</summary>
    /// <param name="cancellationToken">Отмена.</param>
    Task<IReadOnlyDictionary<string, InboxItemState>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Задаёт состояние путям; <see cref="InboxItemState.New"/> удаляет записи.
    /// </summary>
    /// <param name="paths">Полные нормализованные пути.</param>
    /// <param name="state">Новое состояние.</param>
    /// <param name="changedUtc">Когда изменено.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task SetAsync(IReadOnlyList<string> paths, InboxItemState state, DateTimeOffset changedUtc, CancellationToken cancellationToken = default);

    /// <summary>Удаляет записи путей, которых больше нет.</summary>
    /// <param name="paths">Полные пути.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task RemoveAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
}

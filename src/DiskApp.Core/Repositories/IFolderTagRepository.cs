using DiskApp.Core.Domain;

namespace DiskApp.Core.Repositories;

/// <summary>
/// Авто-теги папок (PLAN.md, этап 19, ADR-0012): тег принадлежит папке и наследуется всем внутри неё. Папки хранятся
/// полными нормализованными путями; таких строк десятки, поэтому список читается целиком.
/// </summary>
public interface IFolderTagRepository
{
    /// <summary>Все папки с авто-тегами, теги каждой — по имени.</summary>
    Task<IReadOnlyList<FolderTags>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Заменяет авто-теги папки; пустой список снимает их все.
    /// </summary>
    /// <param name="path">Полный нормализованный путь папки.</param>
    /// <param name="tagIds">Идентификаторы тегов.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task SetAsync(string path, IReadOnlyList<long> tagIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Переносит авто-теги на новые пути папок после переименования (одной транзакцией).
    /// </summary>
    /// <param name="changes">Пары «прежний путь → новый путь»; оба — полные нормализованные пути.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task MovePathsAsync(IReadOnlyList<(string From, string To)> changes, CancellationToken cancellationToken = default);
}

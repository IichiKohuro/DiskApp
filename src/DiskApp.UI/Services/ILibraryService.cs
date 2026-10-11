using DiskApp.UI.Models;

namespace DiskApp.UI.Services;

/// <summary>
/// Избранное, теги и коллекции (PLAN.md, этап 8.7) для страниц «Коллекции» и «Теги» и диалога тегов.
/// </summary>
public interface ILibraryService
{
    /// <summary>Избранное в порядке пользователя.</summary>
    Task<IReadOnlyList<FileEntry>> GetFavoritesAsync(CancellationToken cancellationToken = default);

    /// <summary>Новый порядок избранного: все записи по одному разу.</summary>
    Task ReorderFavoritesAsync(IReadOnlyList<long> entryIds, CancellationToken cancellationToken = default);

    /// <summary>Все теги.</summary>
    Task<IReadOnlyList<TagInfo>> GetTagsAsync(CancellationToken cancellationToken = default);

    /// <summary>Создаёт тег; имя занято — исключение с понятным текстом.</summary>
    Task<TagInfo> CreateTagAsync(string name, string? color, CancellationToken cancellationToken = default);

    /// <summary>Переименовывает тег или меняет цвет.</summary>
    Task<TagInfo> UpdateTagAsync(long id, string name, string? color, CancellationToken cancellationToken = default);

    /// <summary>Удаляет тег у всех записей.</summary>
    Task DeleteTagAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Заменяет набор тегов записи.</summary>
    Task<FileEntry> SetTagsAsync(FileEntry entry, IReadOnlyList<long> tagIds, CancellationToken cancellationToken = default);

    /// <summary>Все коллекции.</summary>
    Task<IReadOnlyList<CollectionInfo>> GetCollectionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Создаёт коллекцию.</summary>
    Task<CollectionInfo> CreateCollectionAsync(string name, CollectionType type, string? query, CancellationToken cancellationToken = default);

    /// <summary>Переименовывает коллекцию или меняет запрос.</summary>
    Task<CollectionInfo> UpdateCollectionAsync(CollectionInfo collection, CancellationToken cancellationToken = default);

    /// <summary>Удаляет коллекцию; файлы не трогаются.</summary>
    Task DeleteCollectionAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Состав коллекции: записи ручной или результаты сохранённого запроса.</summary>
    Task<IReadOnlyList<FileEntry>> GetCollectionItemsAsync(CollectionInfo collection, CancellationToken cancellationToken = default);

    /// <summary>Заменяет состав и порядок ручной коллекции.</summary>
    Task SetCollectionItemsAsync(long collectionId, IReadOnlyList<long> entryIds, CancellationToken cancellationToken = default);

    /// <summary>Записи индекса для путей (импорт списка, перетаскивание).</summary>
    Task<ResolveResult> ResolveAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
}

using DiskApp.Core.Domain;

namespace DiskApp.Core.Repositories;

/// <summary>
/// Коллекции и их элементы. Элементы хранятся только у ручных коллекций; сохранённый запрос выполняется поисковым сервисом.
/// </summary>
public interface ICollectionRepository
{
    /// <summary>Все коллекции по имени.</summary>
    Task<IReadOnlyList<Collection>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Коллекция по идентификатору.</summary>
    Task<Collection?> GetByIdAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Создаёт коллекцию и возвращает её с идентификатором.</summary>
    Task<Collection> AddAsync(Collection collection, CancellationToken cancellationToken = default);

    /// <summary>Сохраняет имя, вид и запрос; возвращает <c>false</c>, если коллекции нет.</summary>
    Task<bool> UpdateAsync(Collection collection, CancellationToken cancellationToken = default);

    /// <summary>Удаляет коллекцию с элементами; возвращает <c>false</c>, если коллекции нет.</summary>
    Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Идентификаторы записей коллекции в порядке пользователя.</summary>
    Task<IReadOnlyList<long>> GetItemsAsync(long collectionId, CancellationToken cancellationToken = default);

    /// <summary>Добавляет запись в конец коллекции; повторный вызов ничего не меняет.</summary>
    Task AddItemAsync(long collectionId, long entryId, CancellationToken cancellationToken = default);

    /// <summary>Убирает запись из коллекции; возвращает <c>false</c>, если её не было.</summary>
    Task<bool> RemoveItemAsync(long collectionId, long entryId, CancellationToken cancellationToken = default);

    /// <summary>Задаёт порядок элементов; не перечисленные идут следом в прежнем порядке.</summary>
    Task ReorderItemsAsync(long collectionId, IReadOnlyList<long> entryIdsInOrder, CancellationToken cancellationToken = default);
}

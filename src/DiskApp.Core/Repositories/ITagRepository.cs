using DiskApp.Core.Domain;

namespace DiskApp.Core.Repositories;

/// <summary>
/// Теги и их привязка к записям. Имя тега уникально без учёта регистра.
/// </summary>
public interface ITagRepository
{
    /// <summary>Все теги по имени.</summary>
    Task<IReadOnlyList<Tag>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Тег по идентификатору.</summary>
    Task<Tag?> GetByIdAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Тег по имени без учёта регистра.</summary>
    Task<Tag?> GetByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Создаёт тег и возвращает его с идентификатором; при совпадении имени возвращает существующий.</summary>
    Task<Tag> AddAsync(string name, string? color = null, CancellationToken cancellationToken = default);

    /// <summary>Переименовывает или перекрашивает тег; возвращает <c>false</c>, если тега нет.</summary>
    Task<bool> UpdateAsync(Tag tag, CancellationToken cancellationToken = default);

    /// <summary>Удаляет тег и все его привязки; возвращает <c>false</c>, если тега нет.</summary>
    Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Привязывает тег к записи; повторный вызов ничего не меняет.</summary>
    Task AssignAsync(long entryId, long tagId, CancellationToken cancellationToken = default);

    /// <summary>Снимает тег с записи; возвращает <c>false</c>, если привязки не было.</summary>
    Task<bool> UnassignAsync(long entryId, long tagId, CancellationToken cancellationToken = default);

    /// <summary>Теги записи по имени.</summary>
    Task<IReadOnlyList<Tag>> GetTagsOfEntryAsync(long entryId, CancellationToken cancellationToken = default);

    /// <summary>Теги нескольких записей: ключ — запись, значение — её теги.</summary>
    Task<IReadOnlyDictionary<long, IReadOnlyList<Tag>>> GetTagsOfEntriesAsync(IReadOnlyList<long> entryIds, CancellationToken cancellationToken = default);

    /// <summary>Идентификаторы записей с тегом.</summary>
    Task<IReadOnlyList<long>> GetEntryIdsWithTagAsync(long tagId, CancellationToken cancellationToken = default);
}

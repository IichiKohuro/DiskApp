using DiskApp.Core.Domain;

namespace DiskApp.Core.Repositories;

/// <summary>
/// Избранное: закреплённые записи в порядке пользователя.
/// </summary>
public interface IFavoriteRepository
{
    /// <summary>Все закреплённые записи в порядке <see cref="Favorite.Order"/>.</summary>
    Task<IReadOnlyList<Favorite>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Закреплена ли запись.</summary>
    Task<bool> ContainsAsync(long entryId, CancellationToken cancellationToken = default);

    /// <summary>Закрепляет запись в конец списка; повторный вызов ничего не меняет.</summary>
    Task<Favorite> AddAsync(long entryId, DateTimeOffset pinnedAtUtc, CancellationToken cancellationToken = default);

    /// <summary>Открепляет запись; возвращает <c>false</c>, если её не было.</summary>
    Task<bool> RemoveAsync(long entryId, CancellationToken cancellationToken = default);

    /// <summary>Задаёт порядок: идентификаторы в новом порядке, остальные идут следом в прежнем.</summary>
    Task ReorderAsync(IReadOnlyList<long> entryIdsInOrder, CancellationToken cancellationToken = default);
}

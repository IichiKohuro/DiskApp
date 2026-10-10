using DiskApp.Core.Domain;

namespace DiskApp.Core.Repositories;

/// <summary>
/// Корни индекса. Удаление корня каскадно удаляет его записи.
/// </summary>
public interface IRootRepository
{
    /// <summary>Все корни в порядке добавления.</summary>
    Task<IReadOnlyList<Root>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Корень по идентификатору.</summary>
    Task<Root?> GetByIdAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Добавляет корень и возвращает его с присвоенным идентификатором.</summary>
    Task<Root> AddAsync(Root root, CancellationToken cancellationToken = default);

    /// <summary>Сохраняет изменённые поля корня; возвращает <c>false</c>, если корня нет.</summary>
    Task<bool> UpdateAsync(Root root, CancellationToken cancellationToken = default);

    /// <summary>Удаляет корень вместе с записями; возвращает <c>false</c>, если корня нет.</summary>
    Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Меняет состояние и сообщение об ошибке.</summary>
    Task SetStatusAsync(long id, RootStatus status, string? errorMessage = null, CancellationToken cancellationToken = default);

    /// <summary>Записывает время завершения сканирования.</summary>
    Task SetLastScanAsync(long id, DateTimeOffset scannedAtUtc, CancellationToken cancellationToken = default);
}

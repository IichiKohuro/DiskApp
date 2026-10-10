using DiskApp.Core.Domain;

namespace DiskApp.Core.Repositories;

/// <summary>
/// Статистика использования записей для frecency и списка недавних.
/// </summary>
public interface IUsageRepository
{
    /// <summary>Отмечает открытие: счётчик +1, время обновляется.</summary>
    Task RecordOpenAsync(long entryId, DateTimeOffset openedAtUtc, CancellationToken cancellationToken = default);

    /// <summary>Статистика записи; <c>null</c>, если её ни разу не открывали.</summary>
    Task<UsageStat?> GetAsync(long entryId, CancellationToken cancellationToken = default);

    /// <summary>Статистика нескольких записей; отсутствующие пропускаются.</summary>
    Task<IReadOnlyList<UsageStat>> GetManyAsync(IReadOnlyList<long> entryIds, CancellationToken cancellationToken = default);

    /// <summary>Последние открытые записи, новые первыми.</summary>
    Task<IReadOnlyList<UsageStat>> GetRecentAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>Удаляет статистику записи.</summary>
    Task RemoveAsync(long entryId, CancellationToken cancellationToken = default);
}

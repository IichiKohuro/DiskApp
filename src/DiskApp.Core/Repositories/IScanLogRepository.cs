namespace DiskApp.Core.Repositories;

/// <summary>
/// Журнал сканирования (<c>scan_log</c>): ошибки доступа и прочие проблемы обхода, которые не прерывают индексацию,
/// но должны быть видны пользователю в диагностике (PLAN.md, этап 2.1).
/// </summary>
public interface IScanLogRepository
{
    /// <summary>Добавляет записи журнала пакетом.</summary>
    Task AddAsync(IReadOnlyList<ScanLogEntry> entries, CancellationToken cancellationToken = default);

    /// <summary>Последние записи корня, новые первыми.</summary>
    Task<IReadOnlyList<ScanLogEntry>> GetRecentAsync(long rootId, int limit, CancellationToken cancellationToken = default);

    /// <summary>Оставляет у корня только <paramref name="keep"/> последних записей; возвращает число удалённых.</summary>
    Task<int> PruneAsync(long rootId, int keep, CancellationToken cancellationToken = default);
}

/// <summary>
/// Запись журнала сканирования.
/// </summary>
/// <param name="RootId">Корень.</param>
/// <param name="ScanId">Сканирование, в котором возникла проблема; 0 — вне сканирования (наблюдатель).</param>
/// <param name="OccurredUtc">Когда.</param>
/// <param name="Level">Важность.</param>
/// <param name="Path">Относительный путь, к которому относится запись; <c>null</c>, если ко всему корню.</param>
/// <param name="Message">Текст для человека.</param>
public sealed record ScanLogEntry(long RootId, long ScanId, DateTimeOffset OccurredUtc, ScanLogLevel Level, string? Path, string Message)
{
    /// <summary>Идентификатор в базе; 0 до сохранения.</summary>
    public long Id { get; init; }
}

/// <summary>
/// Важность записи журнала сканирования.
/// </summary>
public enum ScanLogLevel
{
    /// <summary>Сведения: начало и итог сканирования.</summary>
    Information,

    /// <summary>Предупреждение: папка пропущена, нет доступа.</summary>
    Warning,

    /// <summary>Ошибка: корень недоступен, наблюдатель упал.</summary>
    Error,
}

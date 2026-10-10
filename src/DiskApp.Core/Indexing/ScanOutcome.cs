using DiskApp.Core.Repositories;

namespace DiskApp.Core.Indexing;

/// <summary>
/// Итог полного сканирования или сверки.
/// </summary>
public sealed record ScanOutcome
{
    /// <summary>Вид задачи.</summary>
    public required IndexTaskKind Kind { get; init; }

    /// <summary>Корень.</summary>
    public required long RootId { get; init; }

    /// <summary>Поддерево; пустая строка — весь корень.</summary>
    public string RelPath { get; init; } = string.Empty;

    /// <summary>Чем закончилось.</summary>
    public required ScanOutcomeStatus Status { get; init; }

    /// <summary>Идентификатор сканирования, которым помечены записи.</summary>
    public long ScanId { get; init; }

    /// <summary>Записано в индекс.</summary>
    public UpsertResult Written { get; init; }

    /// <summary>Удалено из индекса записей, которых больше нет на диске.</summary>
    public int Removed { get; init; }

    /// <summary>Найдено записей на диске.</summary>
    public long Found { get; init; }

    /// <summary>Ошибок обхода.</summary>
    public long ErrorCount { get; init; }

    /// <summary>Длительность.</summary>
    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// Исход сканирования.
/// </summary>
public enum ScanOutcomeStatus
{
    /// <summary>Индекс сверен с диском.</summary>
    Completed,

    /// <summary>Корня нет на диске: индекс не тронут, корень следует считать недоступным.</summary>
    RootMissing,

    /// <summary>Поддерева больше нет: его записи удалены.</summary>
    SubtreeMissing,

    /// <summary>Поддерево попало под исключения: его записи удалены.</summary>
    Excluded,
}

/// <summary>
/// Вид задачи индексации; порядок — приоритет (меньше — важнее, PLAN.md, этап 2.4).
/// </summary>
public enum IndexTaskKind
{
    /// <summary>Сверка с диском по разнице: читает всё, пишет только изменения.</summary>
    Reconcile,

    /// <summary>Полное сканирование mark-and-sweep: помечает каждую запись.</summary>
    FullScan,
}

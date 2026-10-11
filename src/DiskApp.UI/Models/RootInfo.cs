namespace DiskApp.UI.Models;

/// <summary>Состояние корня для карточки на главной.</summary>
public enum RootState
{
    /// <summary>Доступен, индекс актуален.</summary>
    Online,

    /// <summary>Недоступен: диск отключён или пропала сеть.</summary>
    Offline,

    /// <summary>Идёт сканирование или сверка.</summary>
    Scanning,

    /// <summary>Индексация приостановлена.</summary>
    Paused,

    /// <summary>Ошибка.</summary>
    Error,

    /// <summary>Выключен в настройках.</summary>
    Disabled,
}

/// <summary>
/// Корень индекса (PLAN.md, этап 8.2).
/// </summary>
public sealed record RootInfo
{
    /// <summary>Идентификатор.</summary>
    public required long Id { get; init; }

    /// <summary>Подпись.</summary>
    public required string Label { get; init; }

    /// <summary>Путь к папке или диску.</summary>
    public required string Path { get; init; }

    /// <summary>Состояние.</summary>
    public RootState State { get; init; }

    /// <summary>Пояснение к состоянию: текст ошибки, причина недоступности.</summary>
    public string? Message { get; init; }

    /// <summary>Когда закончилось последнее полное сканирование.</summary>
    public DateTimeOffset? LastScan { get; init; }

    /// <summary>Сколько записей просмотрено в идущем сканировании.</summary>
    public long? Scanned { get; init; }

    /// <summary>Индексировать скрытые файлы и папки.</summary>
    public bool IncludeHidden { get; init; }

    /// <summary>Индексировать системные файлы и папки.</summary>
    public bool IncludeSystem { get; init; }

    /// <summary>Наибольшая глубина обхода; <c>null</c> — без предела.</summary>
    public int? MaxDepth { get; init; }
}

/// <summary>
/// Сводка корня: файлы, папки, суммарный размер.
/// </summary>
/// <param name="FileCount">Файлов.</param>
/// <param name="DirectoryCount">Папок.</param>
/// <param name="TotalSize">Размер файлов в байтах.</param>
public sealed record RootSummary(long FileCount, long DirectoryCount, long TotalSize);

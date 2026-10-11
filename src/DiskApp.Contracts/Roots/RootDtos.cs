namespace DiskApp.Contracts.Roots;

/// <summary>
/// Корень индексации.
/// </summary>
/// <param name="Id">Идентификатор.</param>
/// <param name="Path">Абсолютный путь.</param>
/// <param name="Label">Подпись.</param>
/// <param name="IsEnabled">Индексируется.</param>
/// <param name="IncludeHidden">Включать скрытые файлы.</param>
/// <param name="IncludeSystem">Включать системные файлы.</param>
/// <param name="MaxDepth">Наибольшая глубина обхода; <c>null</c> — без ограничения.</param>
/// <param name="LastScanUtc">Последнее полное сканирование.</param>
/// <param name="Status">Состояние.</param>
/// <param name="ErrorMessage">Причина ошибки или недоступности.</param>
public sealed record RootDto(
    long Id,
    string Path,
    string Label,
    bool IsEnabled,
    bool IncludeHidden,
    bool IncludeSystem,
    int? MaxDepth,
    DateTimeOffset? LastScanUtc,
    RootStatus Status,
    string? ErrorMessage);

/// <summary>
/// Сводка корня: <c>GET /api/v1/roots/{id}/stats</c>.
/// </summary>
/// <param name="RootId">Корень.</param>
/// <param name="FileCount">Файлов.</param>
/// <param name="DirectoryCount">Папок.</param>
/// <param name="TotalSize">Суммарный размер файлов в байтах.</param>
public sealed record RootStatsDto(long RootId, long FileCount, long DirectoryCount, long TotalSize);

/// <summary>Состояние корня.</summary>
public enum RootStatus
{
    /// <summary>Доступен, индекс актуален.</summary>
    Online,

    /// <summary>Недоступен (диск отключён, сеть); индекс сохранён.</summary>
    Offline,

    /// <summary>Идёт сканирование или сверка.</summary>
    Scanning,

    /// <summary>Индексация приостановлена.</summary>
    Paused,

    /// <summary>Ошибка.</summary>
    Error,
}

/// <summary>
/// Добавление корня: <c>POST /api/v1/roots</c>.
/// </summary>
public sealed record CreateRootRequest
{
    /// <summary>Абсолютный путь к папке или диску.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Подпись; по умолчанию — имя папки.</summary>
    public string? Label { get; set; }

    /// <summary>Включать скрытые файлы.</summary>
    public bool IncludeHidden { get; set; }

    /// <summary>Включать системные файлы.</summary>
    public bool IncludeSystem { get; set; }

    /// <summary>Наибольшая глубина обхода.</summary>
    public int? MaxDepth { get; set; }
}

/// <summary>
/// Изменение корня: <c>PUT /api/v1/roots/{id}</c>. Путь не меняется: для другого пути добавляется новый корень.
/// </summary>
public sealed record UpdateRootRequest
{
    /// <summary>Подпись.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Индексируется.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Включать скрытые файлы.</summary>
    public bool IncludeHidden { get; set; }

    /// <summary>Включать системные файлы.</summary>
    public bool IncludeSystem { get; set; }

    /// <summary>Наибольшая глубина обхода; <c>null</c> — без ограничения.</summary>
    public int? MaxDepth { get; set; }
}

/// <summary>
/// Состояние индексации: <c>GET /api/v1/index/status</c>.
/// </summary>
/// <param name="State">Общее состояние для иконки трея.</param>
/// <param name="IsPaused">Пауза включена.</param>
/// <param name="Roots">Состояние по корням.</param>
/// <param name="TotalEntries">Записей в индексе (только в <c>GET /api/v1/index/status</c>).</param>
public sealed record IndexStatusDto(IndexerState State, bool IsPaused, IReadOnlyList<RootIndexStatusDto> Roots, long? TotalEntries = null);

/// <summary>Общее состояние индексации.</summary>
public enum IndexerState
{
    /// <summary>Работы нет.</summary>
    Idle,

    /// <summary>Идёт сканирование или сверка.</summary>
    Indexing,

    /// <summary>Пауза.</summary>
    Paused,

    /// <summary>Часть корней недоступна.</summary>
    PartiallyOffline,

    /// <summary>Есть корни с ошибкой.</summary>
    Error,
}

/// <summary>
/// Состояние индексации корня.
/// </summary>
/// <param name="RootId">Корень.</param>
/// <param name="Path">Путь.</param>
/// <param name="Label">Подпись.</param>
/// <param name="Status">Состояние.</param>
/// <param name="Message">Пояснение (ошибка, причина недоступности).</param>
/// <param name="RunningTask">Выполняемая задача.</param>
/// <param name="IsQueued">Есть задача в очереди.</param>
/// <param name="Progress">Ход выполняемой задачи.</param>
/// <param name="LastScanUtc">Последнее полное сканирование.</param>
public sealed record RootIndexStatusDto(
    long RootId,
    string Path,
    string Label,
    RootStatus Status,
    string? Message,
    IndexTaskKind? RunningTask,
    bool IsQueued,
    IndexProgressDto? Progress,
    DateTimeOffset? LastScanUtc);

/// <summary>Вид задачи индексации.</summary>
public enum IndexTaskKind
{
    /// <summary>Сверка с диском.</summary>
    Reconcile,

    /// <summary>Полное сканирование.</summary>
    FullScan,
}

/// <summary>
/// Ход индексации корня: событие <c>OnIndexProgress</c>.
/// </summary>
/// <param name="RootId">Корень.</param>
/// <param name="Phase">Фаза.</param>
/// <param name="Scanned">Просмотрено записей.</param>
/// <param name="Written">Записано в индекс.</param>
/// <param name="CurrentPath">Текущая папка.</param>
/// <param name="RatePerSecond">Записей в секунду.</param>
/// <param name="ElapsedSeconds">Прошло секунд.</param>
public sealed record IndexProgressDto(
    long RootId,
    IndexPhase Phase,
    long Scanned,
    long Written,
    string? CurrentPath,
    double RatePerSecond,
    double ElapsedSeconds);

/// <summary>Фаза индексации.</summary>
public enum IndexPhase
{
    /// <summary>Обход файловой системы.</summary>
    Enumerating,

    /// <summary>Запись в индекс.</summary>
    Writing,

    /// <summary>Удаление исчезнувших записей.</summary>
    Sweeping,

    /// <summary>Готово.</summary>
    Done,
}

/// <summary>
/// Смена состояния корня: событие <c>OnRootStatusChanged</c>.
/// </summary>
/// <param name="RootId">Корень.</param>
/// <param name="Status">Новое состояние.</param>
/// <param name="Message">Пояснение.</param>
public sealed record RootStatusChangedDto(long RootId, RootStatus Status, string? Message);

namespace DiskApp.Contracts.Operations;

/// <summary>
/// Постановка файловой операции: <c>POST /api/v1/operations</c>.
/// </summary>
public sealed record OperationRequest
{
    /// <summary>Вид операции.</summary>
    public OperationKind Kind { get; set; }

    /// <summary>Исходные пути (для создания — пусто).</summary>
    public IReadOnlyList<string> Sources { get; set; } = [];

    /// <summary>Целевая папка: для копирования, перемещения и создания.</summary>
    public string? Destination { get; set; }

    /// <summary>Новое имя: для переименования и создания.</summary>
    public string? NewName { get; set; }

    /// <summary>Политика конфликтов имён.</summary>
    public ConflictPolicy Conflicts { get; set; } = ConflictPolicy.Ask;

    /// <summary>Удалить безвозвратно, минуя корзину. Только по явному выбору пользователя.</summary>
    public bool Permanent { get; set; }
}

/// <summary>Вид файловой операции.</summary>
public enum OperationKind
{
    /// <summary>Копирование.</summary>
    Copy,

    /// <summary>Перемещение.</summary>
    Move,

    /// <summary>Переименование.</summary>
    Rename,

    /// <summary>Удаление (в корзину).</summary>
    Delete,

    /// <summary>Создание папки.</summary>
    CreateFolder,

    /// <summary>Создание пустого файла.</summary>
    CreateFile,
}

/// <summary>Политика конфликтов имён.</summary>
public enum ConflictPolicy
{
    /// <summary>Спросить пользователя.</summary>
    Ask,

    /// <summary>Пропустить.</summary>
    Skip,

    /// <summary>Заменить.</summary>
    Overwrite,

    /// <summary>Сохранить оба: <c>имя (2).ext</c>.</summary>
    KeepBoth,
}

/// <summary>Состояние операции.</summary>
public enum OperationState
{
    /// <summary>В очереди.</summary>
    Queued,

    /// <summary>Выполняется.</summary>
    Running,

    /// <summary>Ждёт решения по конфликту.</summary>
    WaitingDecision,

    /// <summary>Выполнена.</summary>
    Completed,

    /// <summary>Завершилась ошибкой.</summary>
    Failed,

    /// <summary>Отменена.</summary>
    Cancelled,
}

/// <summary>
/// Файловая операция: <c>GET /api/v1/operations</c>, <c>GET /api/v1/operations/{id}</c>.
/// </summary>
/// <param name="Id">Идентификатор.</param>
/// <param name="Kind">Вид.</param>
/// <param name="State">Состояние.</param>
/// <param name="Sources">Исходные пути.</param>
/// <param name="Destination">Целевая папка.</param>
/// <param name="CreatedUtc">Поставлена.</param>
/// <param name="CompletedUtc">Завершена.</param>
/// <param name="Progress">Ход.</param>
/// <param name="Message">Ошибка или пояснение.</param>
/// <param name="CanUndo">Результат можно отменить.</param>
/// <param name="PendingConflict">Конфликт, ждущий решения.</param>
public sealed record OperationDto(
    Guid Id,
    OperationKind Kind,
    OperationState State,
    IReadOnlyList<string> Sources,
    string? Destination,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? CompletedUtc,
    OperationProgressDto Progress,
    string? Message,
    bool CanUndo,
    ConflictDto? PendingConflict)
{
    /// <summary>Кто поставил операцию: пользователь или авторазбор по шаблонам рабочих папок (этап 17.5).</summary>
    public OperationOrigin Origin { get; set; }
}

/// <summary>Кто поставил операцию.</summary>
public enum OperationOrigin
{
    /// <summary>Пользователь: интерфейс или API.</summary>
    User,

    /// <summary>Авторазбор по шаблонам рабочих папок.</summary>
    AutoSort,
}

/// <summary>
/// Ход операции: событие <c>OnOperationProgress</c>.
/// </summary>
/// <param name="OperationId">Операция.</param>
/// <param name="ProcessedItems">Обработано элементов.</param>
/// <param name="TotalItems">Всего элементов; 0 — ещё неизвестно.</param>
/// <param name="ProcessedBytes">Обработано байтов.</param>
/// <param name="TotalBytes">Всего байтов; 0 — ещё неизвестно.</param>
/// <param name="CurrentPath">Текущий файл.</param>
/// <param name="BytesPerSecond">Скорость.</param>
/// <param name="RemainingSeconds">Оценка оставшегося времени.</param>
public sealed record OperationProgressDto(
    Guid OperationId,
    long ProcessedItems,
    long TotalItems,
    long ProcessedBytes,
    long TotalBytes,
    string? CurrentPath,
    double BytesPerSecond,
    double? RemainingSeconds);

/// <summary>
/// Конфликт имён: событие <c>OnOperationNeedsDecision</c>.
/// </summary>
/// <param name="OperationId">Операция.</param>
/// <param name="SourcePath">Что копируется или переносится.</param>
/// <param name="TargetPath">Что уже есть в цели.</param>
/// <param name="SourceSize">Размер источника.</param>
/// <param name="SourceModifiedUtc">Изменён источник.</param>
/// <param name="TargetSize">Размер цели.</param>
/// <param name="TargetModifiedUtc">Изменена цель.</param>
public sealed record ConflictDto(
    Guid OperationId,
    string SourcePath,
    string TargetPath,
    long SourceSize,
    DateTimeOffset SourceModifiedUtc,
    long TargetSize,
    DateTimeOffset TargetModifiedUtc);

/// <summary>
/// Решение по конфликту: <c>POST /api/v1/operations/{id}/decision</c>.
/// </summary>
public sealed record ConflictDecisionDto
{
    /// <summary>Что сделать.</summary>
    public ConflictResolution Resolution { get; set; }

    /// <summary>Применить ко всем следующим конфликтам этой операции.</summary>
    public bool ApplyToAll { get; set; }
}

/// <summary>Решение по конфликту.</summary>
public enum ConflictResolution
{
    /// <summary>Пропустить.</summary>
    Skip,

    /// <summary>Заменить.</summary>
    Overwrite,

    /// <summary>Сохранить оба.</summary>
    KeepBoth,

    /// <summary>Отменить операцию.</summary>
    Cancel,
}

/// <summary>
/// Итог загрузки файлов: <c>POST /api/v1/upload</c>.
/// </summary>
/// <param name="Saved">Сохранённые файлы (полные пути).</param>
/// <param name="Skipped">Пропущенные из-за конфликта имён (имена из запроса).</param>
public sealed record UploadResponse(IReadOnlyList<string> Saved, IReadOnlyList<string> Skipped);

/// <summary>
/// Завершение операции: событие <c>OnOperationCompleted</c>.
/// </summary>
/// <param name="OperationId">Операция.</param>
/// <param name="State">Итоговое состояние.</param>
/// <param name="Message">Ошибка или пояснение.</param>
public sealed record OperationCompletedDto(Guid OperationId, OperationState State, string? Message);

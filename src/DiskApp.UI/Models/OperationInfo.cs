namespace DiskApp.UI.Models;

/// <summary>Вид файловой операции (совпадает по именам с контрактом API).</summary>
public enum FileOperationKind
{
    /// <summary>Копирование.</summary>
    Copy,

    /// <summary>Перемещение.</summary>
    Move,

    /// <summary>Переименование.</summary>
    Rename,

    /// <summary>Удаление в корзину.</summary>
    Delete,

    /// <summary>Создание папки.</summary>
    CreateFolder,

    /// <summary>Создание файла.</summary>
    CreateFile,
}

/// <summary>Состояние файловой операции (совпадает по именам с контрактом API).</summary>
public enum FileOperationState
{
    /// <summary>В очереди.</summary>
    Queued,

    /// <summary>Выполняется.</summary>
    Running,

    /// <summary>Ждёт решения по конфликту имён.</summary>
    WaitingDecision,

    /// <summary>Выполнена.</summary>
    Completed,

    /// <summary>Завершилась ошибкой.</summary>
    Failed,

    /// <summary>Отменена.</summary>
    Cancelled,
}

/// <summary>Решение по конфликту имён.</summary>
public enum ConflictChoice
{
    /// <summary>Пропустить файл.</summary>
    Skip,

    /// <summary>Заменить: старый файл уходит в корзину.</summary>
    Overwrite,

    /// <summary>Оставить оба: новый получает номер.</summary>
    KeepBoth,

    /// <summary>Отменить всю операцию.</summary>
    Cancel,
}

/// <summary>
/// Файловая операция в очереди сервера (PLAN.md, этап 8.4).
/// </summary>
public sealed record OperationInfo
{
    /// <summary>Идентификатор.</summary>
    public required Guid Id { get; init; }

    /// <summary>Вид.</summary>
    public FileOperationKind Kind { get; init; }

    /// <summary>Состояние.</summary>
    public FileOperationState State { get; init; }

    /// <summary>Исходные пути.</summary>
    public IReadOnlyList<string> Sources { get; init; } = [];

    /// <summary>Папка назначения.</summary>
    public string? Destination { get; init; }

    /// <summary>Когда поставлена.</summary>
    public DateTimeOffset Created { get; init; }

    /// <summary>Когда завершилась.</summary>
    public DateTimeOffset? Completed { get; init; }

    /// <summary>Обработано элементов.</summary>
    public long ProcessedItems { get; init; }

    /// <summary>Всего элементов; 0 — ещё неизвестно.</summary>
    public long TotalItems { get; init; }

    /// <summary>Обработано байтов.</summary>
    public long ProcessedBytes { get; init; }

    /// <summary>Всего байтов; 0 — ещё неизвестно.</summary>
    public long TotalBytes { get; init; }

    /// <summary>Текущий файл.</summary>
    public string? CurrentPath { get; init; }

    /// <summary>Скорость, байт в секунду.</summary>
    public double BytesPerSecond { get; init; }

    /// <summary>Оценка оставшегося времени, секунды.</summary>
    public double? RemainingSeconds { get; init; }

    /// <summary>Сообщение: ошибка или итог.</summary>
    public string? Message { get; init; }

    /// <summary>Результат можно отменить.</summary>
    public bool CanUndo { get; init; }

    /// <summary>Конфликт, ждущий решения.</summary>
    public ConflictInfo? PendingConflict { get; init; }

    /// <summary>Операция ещё идёт.</summary>
    public bool IsActive => State is FileOperationState.Queued or FileOperationState.Running or FileOperationState.WaitingDecision;

    /// <summary>Доля выполнения от 0 до 1; <c>null</c> — объём неизвестен.</summary>
    public double? Fraction => TotalBytes > 0 ? Math.Clamp((double)ProcessedBytes / TotalBytes, 0, 1)
        : TotalItems > 0 ? Math.Clamp((double)ProcessedItems / TotalItems, 0, 1)
        : null;
}

/// <summary>
/// Конфликт имён: что копируется и что уже лежит в цели.
/// </summary>
/// <param name="OperationId">Операция.</param>
/// <param name="SourcePath">Источник.</param>
/// <param name="TargetPath">Файл в цели.</param>
/// <param name="SourceSize">Размер источника.</param>
/// <param name="SourceModified">Изменён источник.</param>
/// <param name="TargetSize">Размер файла в цели.</param>
/// <param name="TargetModified">Изменён файл в цели.</param>
public sealed record ConflictInfo(Guid OperationId, string SourcePath, string TargetPath, long SourceSize, DateTimeOffset SourceModified, long TargetSize, DateTimeOffset TargetModified);

/// <summary>
/// Ответ на конфликт.
/// </summary>
/// <param name="Choice">Решение.</param>
/// <param name="ApplyToAll">Применить ко всем следующим конфликтам операции.</param>
public sealed record ConflictAnswer(ConflictChoice Choice, bool ApplyToAll);

/// <summary>
/// Новая операция для очереди.
/// </summary>
/// <param name="Kind">Вид.</param>
/// <param name="Sources">Исходные пути (для создания — пусто).</param>
/// <param name="Destination">Папка назначения.</param>
/// <param name="NewName">Имя для создания и переименования.</param>
public sealed record NewOperation(FileOperationKind Kind, IReadOnlyList<string> Sources, string? Destination, string? NewName = null);

/// <summary>
/// Перенос из накопителя в рабочую папку (PLAN.md, этап 17).
/// </summary>
/// <param name="Sources">Что перенести.</param>
/// <param name="Destination">Рабочая папка или её подпапка.</param>
/// <param name="NewFolder">Новая подпапка внутри <paramref name="Destination"/>; <c>null</c> — прямо в неё.</param>
/// <param name="Tags">Теги для перенесённого.</param>
/// <param name="Names">Новые имена по порядку <paramref name="Sources"/>; <c>null</c> — прежние.</param>
public sealed record SortOperation(IReadOnlyList<string> Sources, string Destination, string? NewFolder, IReadOnlyList<string> Tags, IReadOnlyList<string?>? Names = null);

/// <summary>
/// Бросок файлов в папку-накопитель из окна Drag&amp;Drop (PLAN.md, этап 18.2).
/// </summary>
/// <param name="Sources">Что бросили; может лежать вне корней.</param>
/// <param name="Destination">Папка-накопитель.</param>
/// <param name="Copy">Копировать; иначе — перенести.</param>
public sealed record DropOperation(IReadOnlyList<string> Sources, string Destination, bool Copy);

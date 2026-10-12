namespace DiskApp.Core.Operations;

/// <summary>Вид файловой операции.</summary>
public enum FileOperationKind
{
    /// <summary>Копирование в папку.</summary>
    Copy,

    /// <summary>Перемещение в папку.</summary>
    Move,

    /// <summary>Переименование.</summary>
    Rename,

    /// <summary>Удаление: в корзину или, по явному флагу, безвозвратно.</summary>
    Delete,

    /// <summary>Создание папки.</summary>
    CreateFolder,

    /// <summary>Создание пустого файла.</summary>
    CreateFile,
}

/// <summary>Политика конфликтов имён.</summary>
public enum ConflictPolicy
{
    /// <summary>Спросить пользователя: операция ждёт решения.</summary>
    Ask,

    /// <summary>Пропустить.</summary>
    Skip,

    /// <summary>Заменить: существующий файл уходит в корзину, папки сливаются.</summary>
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

    /// <summary>Завершилась ошибкой (часть элементов могла выполниться).</summary>
    Failed,

    /// <summary>Отменена.</summary>
    Cancelled,
}

/// <summary>Кто поставил операцию.</summary>
public enum OperationOrigin
{
    /// <summary>Пользователь: интерфейс или API.</summary>
    User,

    /// <summary>Авторазбор по шаблонам рабочих папок (PLAN.md, этап 17.5).</summary>
    AutoSort,
}

/// <summary>Решение по конфликту.</summary>
public enum ConflictResolution
{
    /// <summary>Пропустить элемент.</summary>
    Skip,

    /// <summary>Заменить.</summary>
    Overwrite,

    /// <summary>Сохранить оба.</summary>
    KeepBoth,

    /// <summary>Отменить операцию.</summary>
    Cancel,
}

/// <summary>
/// Запрос файловой операции. Пути абсолютные.
/// </summary>
/// <param name="Kind">Вид.</param>
/// <param name="Sources">Исходные пути; для создания — пусто.</param>
/// <param name="Destination">Целевая папка: копирование, перемещение, создание.</param>
/// <param name="NewName">Новое имя: переименование и создание.</param>
/// <param name="Conflicts">Политика конфликтов.</param>
/// <param name="Permanent">Удалить безвозвратно, минуя корзину.</param>
/// <param name="TargetNames">
/// Имена на месте назначения для копирования и переноса, по порядку <paramref name="Sources"/>; <c>null</c> в списке или
/// без списка — прежнее имя (разбор с переименованием, этап 17).
/// </param>
/// <param name="Origin">Кто поставил: пользователь или авторазбор (этап 17.5).</param>
public sealed record FileOperationRequest(
    FileOperationKind Kind,
    IReadOnlyList<string> Sources,
    string? Destination = null,
    string? NewName = null,
    ConflictPolicy Conflicts = ConflictPolicy.Ask,
    bool Permanent = false,
    IReadOnlyList<string?>? TargetNames = null,
    OperationOrigin Origin = OperationOrigin.User);

/// <summary>
/// Ход операции.
/// </summary>
/// <param name="ProcessedItems">Обработано элементов.</param>
/// <param name="TotalItems">Всего элементов.</param>
/// <param name="ProcessedBytes">Обработано байтов.</param>
/// <param name="TotalBytes">Всего байтов.</param>
/// <param name="CurrentPath">Текущий файл.</param>
/// <param name="BytesPerSecond">Средняя скорость.</param>
/// <param name="Remaining">Оценка оставшегося времени.</param>
public sealed record OperationProgress(
    long ProcessedItems,
    long TotalItems,
    long ProcessedBytes,
    long TotalBytes,
    string? CurrentPath,
    double BytesPerSecond,
    TimeSpan? Remaining);

/// <summary>
/// Конфликт имён, ждущий решения.
/// </summary>
/// <param name="OperationId">Операция.</param>
/// <param name="SourcePath">Источник.</param>
/// <param name="TargetPath">Существующая цель.</param>
/// <param name="SourceSize">Размер источника.</param>
/// <param name="SourceModifiedUtc">Изменён источник.</param>
/// <param name="TargetSize">Размер цели.</param>
/// <param name="TargetModifiedUtc">Изменена цель.</param>
public sealed record OperationConflict(
    Guid OperationId,
    string SourcePath,
    string TargetPath,
    long SourceSize,
    DateTimeOffset SourceModifiedUtc,
    long TargetSize,
    DateTimeOffset TargetModifiedUtc);

/// <summary>
/// Снимок операции для API и интерфейса.
/// </summary>
/// <param name="Id">Идентификатор.</param>
/// <param name="Kind">Вид.</param>
/// <param name="State">Состояние.</param>
/// <param name="Sources">Исходные пути.</param>
/// <param name="Destination">Целевая папка.</param>
/// <param name="CreatedUtc">Поставлена.</param>
/// <param name="CompletedUtc">Завершена.</param>
/// <param name="Progress">Ход.</param>
/// <param name="Message">Ошибка или сводка.</param>
/// <param name="CanUndo">Результат можно отменить.</param>
/// <param name="PendingConflict">Конфликт, ждущий решения.</param>
public sealed record FileOperationInfo(
    Guid Id,
    FileOperationKind Kind,
    OperationState State,
    IReadOnlyList<string> Sources,
    string? Destination,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? CompletedUtc,
    OperationProgress Progress,
    string? Message,
    bool CanUndo,
    OperationConflict? PendingConflict)
{
    /// <summary>Операция ещё не завершена.</summary>
    public bool IsActive => State is OperationState.Queued or OperationState.Running or OperationState.WaitingDecision;

    /// <summary>
    /// Что куда перенесено или переименовано (с учётом «оставить оба»); заполняется, когда операция завершилась.
    /// </summary>
    public IReadOnlyList<MovedItem> Moved { get; init; } = [];

    /// <summary>Кто поставил операцию: пользователь или авторазбор.</summary>
    public OperationOrigin Origin { get; init; }
}

/// <summary>
/// Перенесённый элемент: прежний и новый путь.
/// </summary>
/// <param name="From">Откуда.</param>
/// <param name="To">Куда, с итоговым именем.</param>
public sealed record MovedItem(string From, string To);

/// <summary>
/// Параметры сервиса операций.
/// </summary>
public sealed record FileOperationOptions
{
    /// <summary>Буфер копирования.</summary>
    public int BufferSize { get; init; } = 1024 * 1024;

    /// <summary>Как часто публиковать ход.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Сколько завершённых операций помнить.</summary>
    public int HistoryLimit { get; init; } = 100;

    /// <summary>
    /// Лежат ли два пути на одном томе: на одном томе перемещение — переименование, иначе — копирование и удаление.
    /// По умолчанию сравниваются корни путей (буква диска, сетевая папка).
    /// </summary>
    public Func<string, string, bool>? SameVolume { get; init; }
}

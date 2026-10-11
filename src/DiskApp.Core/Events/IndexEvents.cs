using DiskApp.Core.Domain;

namespace DiskApp.Core.Events;

/// <summary>
/// Маркер события внутренней шины (<see cref="IIndexEventBus"/>). Сервер транслирует события в SignalR (этап 4.4).
/// </summary>
public interface IIndexEvent
{
}

/// <summary>
/// Изменения записей одного корня пакетом: сканер и наблюдатель агрегируют их по папкам, чтобы не слать событие на каждый файл.
/// </summary>
/// <param name="RootId">Корень.</param>
/// <param name="Changes">Изменения в порядке применения.</param>
public sealed record EntriesChangedEvent(long RootId, IReadOnlyList<EntryChange> Changes) : IIndexEvent;

/// <summary>
/// Одно изменение записи.
/// </summary>
/// <param name="Kind">Что произошло.</param>
/// <param name="RelPath">Относительный путь записи после изменения.</param>
/// <param name="EntryId">Идентификатор записи; <c>null</c>, если запись удалена и идентификатор уже не действителен.</param>
/// <param name="IsDirectory">Запись — папка; для папки изменение касается всего поддерева.</param>
public sealed record EntryChange(EntryChangeKind Kind, string RelPath, long? EntryId, bool IsDirectory);

/// <summary>
/// Вид изменения записи.
/// </summary>
public enum EntryChangeKind
{
    /// <summary>Добавлена.</summary>
    Added,

    /// <summary>Изменились метаданные.</summary>
    Modified,

    /// <summary>Переименована или перенесена; путь — новый.</summary>
    Renamed,

    /// <summary>Удалена.</summary>
    Removed,
}

/// <summary>
/// Ход индексации корня; публикуется не чаще нескольких раз в секунду.
/// </summary>
/// <param name="RootId">Корень.</param>
/// <param name="Phase">Фаза.</param>
/// <param name="Scanned">Просмотрено записей.</param>
/// <param name="Written">Записано в индекс.</param>
/// <param name="CurrentPath">Текущая папка; <c>null</c> между фазами.</param>
/// <param name="RatePerSecond">Скорость, записей в секунду.</param>
/// <param name="Elapsed">Прошло с начала.</param>
public sealed record IndexProgressEvent(
    long RootId,
    IndexPhase Phase,
    long Scanned,
    long Written,
    string? CurrentPath,
    double RatePerSecond,
    TimeSpan Elapsed) : IIndexEvent;

/// <summary>
/// Фазы индексации (PLAN.md, этап 2.1).
/// </summary>
public enum IndexPhase
{
    /// <summary>Обход файловой системы.</summary>
    Enumerating,

    /// <summary>Запись пакетов в базу.</summary>
    Writing,

    /// <summary>Удаление записей, которых больше нет на диске.</summary>
    Sweeping,

    /// <summary>Завершено.</summary>
    Done,
}

/// <summary>
/// Смена состояния корня.
/// </summary>
/// <param name="RootId">Корень.</param>
/// <param name="Status">Новое состояние.</param>
/// <param name="Message">Пояснение для <see cref="RootStatus.Error"/> и <see cref="RootStatus.Offline"/>; иначе <c>null</c>.</param>
public sealed record RootStatusChangedEvent(long RootId, RootStatus Status, string? Message) : IIndexEvent;

/// <summary>
/// Ход файловой операции (этап 4.5).
/// </summary>
/// <param name="OperationId">Операция.</param>
/// <param name="ProcessedItems">Обработано элементов.</param>
/// <param name="TotalItems">Всего элементов; 0, если ещё неизвестно.</param>
/// <param name="ProcessedBytes">Обработано байтов.</param>
/// <param name="TotalBytes">Всего байтов; 0, если ещё неизвестно.</param>
/// <param name="CurrentPath">Текущий файл; <c>null</c> между файлами.</param>
/// <param name="BytesPerSecond">Средняя скорость с начала операции.</param>
/// <param name="Remaining">Оценка оставшегося времени; <c>null</c>, если её нет.</param>
public sealed record OperationProgressEvent(
    Guid OperationId,
    long ProcessedItems,
    long TotalItems,
    long ProcessedBytes,
    long TotalBytes,
    string? CurrentPath,
    double BytesPerSecond = 0,
    TimeSpan? Remaining = null) : IIndexEvent
{
    /// <summary>Доля выполнения от 0 до 1 по байтам, если они известны, иначе по элементам; 0, если ничего не известно.</summary>
    public double Fraction => TotalBytes > 0
        ? Math.Clamp((double)ProcessedBytes / TotalBytes, 0, 1)
        : TotalItems > 0 ? Math.Clamp((double)ProcessedItems / TotalItems, 0, 1) : 0;
}

/// <summary>
/// Завершение файловой операции.
/// </summary>
/// <param name="OperationId">Операция.</param>
/// <param name="Outcome">Чем закончилась.</param>
/// <param name="Message">Сообщение об ошибке или сводка; <c>null</c>, если нечего сказать.</param>
public sealed record OperationCompletedEvent(Guid OperationId, OperationOutcome Outcome, string? Message) : IIndexEvent;

/// <summary>
/// Файловая операция ждёт решения по конфликту имён.
/// </summary>
/// <param name="OperationId">Операция.</param>
/// <param name="SourcePath">Что копируется или переносится.</param>
/// <param name="TargetPath">Что уже есть в цели.</param>
/// <param name="SourceSize">Размер источника.</param>
/// <param name="SourceModifiedUtc">Изменён источник.</param>
/// <param name="TargetSize">Размер цели.</param>
/// <param name="TargetModifiedUtc">Изменена цель.</param>
public sealed record OperationNeedsDecisionEvent(
    Guid OperationId,
    string SourcePath,
    string TargetPath,
    long SourceSize,
    DateTimeOffset SourceModifiedUtc,
    long TargetSize,
    DateTimeOffset TargetModifiedUtc) : IIndexEvent;

/// <summary>
/// Исход файловой операции.
/// </summary>
public enum OperationOutcome
{
    /// <summary>Выполнена.</summary>
    Completed,

    /// <summary>Завершилась ошибкой.</summary>
    Failed,

    /// <summary>Отменена пользователем.</summary>
    Cancelled,
}

/// <summary>
/// Наблюдатель увидел переименование или перенос внутри корня, и индекс перенёс поддерево (PLAN.md, этап 17.2): по нему
/// пути накопителей и рабочих папок в настройках следуют за папкой, переименованной в Проводнике.
/// </summary>
/// <param name="RootId">Корень.</param>
/// <param name="OldPath">Полный путь до переименования.</param>
/// <param name="NewPath">Полный путь после.</param>
/// <param name="IsDirectory">Переименована папка.</param>
public sealed record EntryRenamedEvent(long RootId, string OldPath, string NewPath, bool IsDirectory) : IIndexEvent;

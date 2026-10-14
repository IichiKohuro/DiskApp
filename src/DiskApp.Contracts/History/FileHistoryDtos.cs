using DiskApp.Contracts.Operations;

namespace DiskApp.Contracts.History;

/// <summary>Что случилось с файлом (история файлов, PLAN.md, этап 20); значения совпадают с хранимыми в базе.</summary>
public enum FileEventKind
{
    /// <summary>Появился: создан, скачан, скопирован или перенесён извне корней.</summary>
    Created = 1,

    /// <summary>Переименован в той же папке.</summary>
    Renamed = 2,

    /// <summary>Перенесён в другую папку.</summary>
    Moved = 3,

    /// <summary>Удалён в корзину.</summary>
    Recycled = 4,

    /// <summary>Удалён навсегда или исчез неизвестно куда.</summary>
    Deleted = 5,

    /// <summary>Восстановлен из корзины.</summary>
    Restored = 6,

    /// <summary>Изменены свои теги.</summary>
    TagsChanged = 7,

    /// <summary>Сводка индекса: корень проиндексирован целиком или убран.</summary>
    IndexSummary = 8,
}

/// <summary>Кто сделал.</summary>
public enum FileEventSource
{
    /// <summary>DiskApp.</summary>
    DiskApp = 1,

    /// <summary>Страница «Разбор».</summary>
    Sorting = 2,

    /// <summary>Авторазбор.</summary>
    AutoSort = 3,

    /// <summary>Проводник или другая программа.</summary>
    Explorer = 4,

    /// <summary>Сверка с диском: сделанное, пока DiskApp не работал.</summary>
    Reconcile = 5,
}

/// <summary>Событие истории файлов.</summary>
/// <param name="Id">Номер; растёт со временем.</param>
/// <param name="TimeUtc">Когда.</param>
/// <param name="Kind">Что случилось.</param>
/// <param name="Source">Кто сделал.</param>
/// <param name="Path">Где файл после события; у удалённого — где был.</param>
/// <param name="OldPath">Где был до переименования или переноса.</param>
/// <param name="Name">Имя после события.</param>
/// <param name="OldName">Имя до события.</param>
/// <param name="IsDirectory">Папка.</param>
/// <param name="Size">Размер файла; у сводки индекса — число записей.</param>
/// <param name="OperationId">Операция DiskApp.</param>
/// <param name="Details">Теги «+тег, −тег», путь в корзине или причина сводки.</param>
/// <param name="CanRevert">Можно «Вернуть на место» или «Восстановить» — по виду события.</param>
public sealed record FileEventDto(
    long Id,
    DateTimeOffset TimeUtc,
    FileEventKind Kind,
    FileEventSource Source,
    string Path,
    string? OldPath,
    string Name,
    string? OldName,
    bool IsDirectory,
    long Size,
    Guid? OperationId,
    string? Details,
    bool CanRevert);

/// <summary>
/// Лента истории: <c>GET /api/v1/files/history</c>; следующая страница — с <c>before</c> = <see cref="NextBefore"/>.
/// </summary>
/// <param name="Items">События, новые сверху.</param>
/// <param name="NextBefore">Номер для следующей страницы; <c>null</c> — дальше ничего.</param>
public sealed record FileHistoryPageDto(IReadOnlyList<FileEventDto> Items, long? NextBefore);

/// <summary>
/// Итог «Вернуть на место» или «Восстановить»: <c>POST /api/v1/files/history/{id}/revert</c>.
/// </summary>
/// <param name="Operation">Поставленная операция возврата.</param>
/// <param name="Restored">Событие восстановления из корзины.</param>
public sealed record FileHistoryRevertDto(OperationDto? Operation, FileEventDto? Restored);

/// <summary>В историю файлов записаны новые события (сообщение хаба).</summary>
/// <param name="Count">Сколько.</param>
public sealed record FileHistoryChangedDto(int Count);

/// <summary>Отбор ленты истории для клиента: параметры <c>GET /api/v1/files/history</c>.</summary>
public sealed record FileHistoryFilter
{
    /// <summary>События внутри папки.</summary>
    public string? Folder { get; init; }

    /// <summary>Имя или прежнее имя содержит.</summary>
    public string? Text { get; init; }

    /// <summary>Только эти виды.</summary>
    public IReadOnlyList<FileEventKind> Kinds { get; init; } = [];

    /// <summary>Только эти источники.</summary>
    public IReadOnlyList<FileEventSource> Sources { get; init; } = [];

    /// <summary>Не раньше.</summary>
    public DateTimeOffset? FromUtc { get; init; }

    /// <summary>Раньше.</summary>
    public DateTimeOffset? ToUtc { get; init; }

    /// <summary>Номер события, с которого продолжать.</summary>
    public long? Before { get; init; }

    /// <summary>Сколько событий.</summary>
    public int Limit { get; init; } = 100;
}

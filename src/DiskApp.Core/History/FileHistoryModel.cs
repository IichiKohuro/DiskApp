namespace DiskApp.Core.History;

/// <summary>Что случилось с файлом или папкой (история файлов, PLAN.md, этап 20).</summary>
public enum FileEventKind
{
    /// <summary>Появился: создан, скачан, скопирован или перенесён извне корней.</summary>
    Created = 1,

    /// <summary>Переименован в той же папке.</summary>
    Renamed = 2,

    /// <summary>Перенесён в другую папку (возможно, с новым именем).</summary>
    Moved = 3,

    /// <summary>Удалён в корзину.</summary>
    Recycled = 4,

    /// <summary>Удалён навсегда или исчез неизвестно куда.</summary>
    Deleted = 5,

    /// <summary>Восстановлен из корзины.</summary>
    Restored = 6,

    /// <summary>Изменены свои теги.</summary>
    TagsChanged = 7,

    /// <summary>
    /// Сводка индекса: корень проиндексирован целиком (первое сканирование, пересборка) или убран — массовые изменения
    /// одной записью; путь — корень, размер — число записей.
    /// </summary>
    IndexSummary = 8,
}

/// <summary>Кто сделал.</summary>
public enum FileEventSource
{
    /// <summary>DiskApp: операция пользователя («Обзор», поиск, окно Drag&amp;Drop, «Вернуть на место»).</summary>
    DiskApp = 1,

    /// <summary>DiskApp: перенос со страницы «Разбор».</summary>
    Sorting = 2,

    /// <summary>DiskApp: авторазбор по шаблонам рабочих папок.</summary>
    AutoSort = 3,

    /// <summary>Проводник или другая программа, пока DiskApp работал (наблюдатель).</summary>
    Explorer = 4,

    /// <summary>Сверка: изменения, найденные при сверке с диском — например, сделанные, пока DiskApp не работал.</summary>
    Reconcile = 5,
}

/// <summary>Событие истории файлов.</summary>
/// <param name="Id">Номер; растёт со временем.</param>
/// <param name="TimeUtc">Когда записано.</param>
/// <param name="Kind">Что случилось.</param>
/// <param name="Source">Кто сделал.</param>
/// <param name="Path">Где файл после события; у удалённого — где он был.</param>
/// <param name="OldPath">Где файл был до переименования или переноса; у копии — откуда скопирован.</param>
/// <param name="IsDirectory">Это папка.</param>
/// <param name="Size">Размер файла; у папки 0.</param>
/// <param name="OperationId">Операция DiskApp, если событие от неё.</param>
/// <param name="Details">
/// Подробности по виду: у <see cref="FileEventKind.TagsChanged"/> — «+тег» и «−тег» через запятую, у
/// <see cref="FileEventKind.Recycled"/> — путь файла в корзине (для «Восстановить»), у <see cref="FileEventKind.IndexSummary"/>
/// — причина.
/// </param>
public sealed record FileEvent(
    long Id,
    DateTimeOffset TimeUtc,
    FileEventKind Kind,
    FileEventSource Source,
    string Path,
    string? OldPath,
    bool IsDirectory,
    long Size,
    Guid? OperationId = null,
    string? Details = null)
{
    /// <summary>Имя после события.</summary>
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/'));

    /// <summary>Имя до события; <c>null</c> — не менялось или неизвестно.</summary>
    public string? OldName => OldPath is null ? null : System.IO.Path.GetFileName(OldPath.TrimEnd('\\', '/'));
}

/// <summary>Отбор событий истории.</summary>
public sealed record FileHistoryQuery
{
    /// <summary>Наибольшее число событий в ответе.</summary>
    public const int MaxLimit = 500;

    /// <summary>События внутри папки (путь или прежний путь в ней); <c>null</c> — везде.</summary>
    public string? Folder { get; init; }

    /// <summary>Имя или прежнее имя содержит эту строку (без учёта регистра); <c>null</c> — любое.</summary>
    public string? Text { get; init; }

    /// <summary>Только эти виды; пусто — все.</summary>
    public IReadOnlyList<FileEventKind> Kinds { get; init; } = [];

    /// <summary>Только эти источники; пусто — все.</summary>
    public IReadOnlyList<FileEventSource> Sources { get; init; } = [];

    /// <summary>Не раньше.</summary>
    public DateTimeOffset? FromUtc { get; init; }

    /// <summary>Раньше этого.</summary>
    public DateTimeOffset? ToUtc { get; init; }

    /// <summary>Номер события, с которого продолжать (не включая); <c>null</c> — с самых новых.</summary>
    public long? BeforeId { get; init; }

    /// <summary>Сколько событий вернуть.</summary>
    public int Limit { get; init; } = 100;
}

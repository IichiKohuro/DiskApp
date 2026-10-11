using DiskApp.Contracts.Library;

namespace DiskApp.Contracts.Entries;

/// <summary>
/// Запись индекса — файл или папка. В результатах поиска дополнительно заполнены оценка, тип совпадения и подсветка.
/// </summary>
public sealed record EntryDto
{
    /// <summary>Идентификатор.</summary>
    public long Id { get; init; }

    /// <summary>Корень.</summary>
    public long RootId { get; init; }

    /// <summary>Родительская папка; <c>null</c> — запись в корне.</summary>
    public long? ParentId { get; init; }

    /// <summary>Имя.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Полный путь.</summary>
    public string FullPath { get; init; } = string.Empty;

    /// <summary>Путь относительно корня.</summary>
    public string RelPath { get; init; } = string.Empty;

    /// <summary>Расширение без точки в нижнем регистре.</summary>
    public string Extension { get; init; } = string.Empty;

    /// <summary>Папка.</summary>
    public bool IsDirectory { get; init; }

    /// <summary>Размер в байтах; у папок 0.</summary>
    public long Size { get; init; }

    /// <summary>Создан.</summary>
    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Изменён.</summary>
    public DateTimeOffset ModifiedUtc { get; init; }

    /// <summary>Вид файла.</summary>
    public FileKind Kind { get; init; }

    /// <summary>
    /// Ключ иконки для кэша клиента: <c>folder</c>, <c>ext:pdf</c>, <c>file</c>; у файлов со своей иконкой
    /// (exe, ярлыки, ico) — <c>entry:{id}</c>. Картинка — <c>GET /api/v1/entries/{id}/icon</c>.
    /// </summary>
    public string IconKey { get; init; } = string.Empty;

    /// <summary>Скрытый.</summary>
    public bool IsHidden { get; init; }

    /// <summary>Облачная заглушка (OneDrive «по запросу»): содержимое не на диске.</summary>
    public bool IsCloudPlaceholder { get; init; }

    /// <summary>В избранном.</summary>
    public bool IsFavorite { get; init; }

    /// <summary>Сколько раз открывали.</summary>
    public int OpenCount { get; init; }

    /// <summary>Когда открывали последний раз.</summary>
    public DateTimeOffset? LastOpenedUtc { get; init; }

    /// <summary>Теги записи; <c>null</c> — тегов нет.</summary>
    public IReadOnlyList<TagDto>? Tags { get; init; }

    /// <summary>Оценка ранжирования (только в поиске).</summary>
    public double? Score { get; init; }

    /// <summary>Тип совпадения с запросом (только в поиске).</summary>
    public MatchKind? Match { get; init; }

    /// <summary>Диапазоны совпадений в имени (только в поиске).</summary>
    public IReadOnlyList<TextRangeDto>? NameHighlights { get; init; }

    /// <summary>Диапазоны совпадений в папке полного пути (только в поиске).</summary>
    public IReadOnlyList<TextRangeDto>? PathHighlights { get; init; }

    /// <summary>Объяснение оценки для отладки (по запросу).</summary>
    public string? RankReason { get; init; }
}

/// <summary>Вид файла.</summary>
public enum FileKind
{
    /// <summary>Прочее.</summary>
    Other,

    /// <summary>Папка.</summary>
    Folder,

    /// <summary>Изображение.</summary>
    Image,

    /// <summary>Видео.</summary>
    Video,

    /// <summary>Аудио.</summary>
    Audio,

    /// <summary>Документ.</summary>
    Document,

    /// <summary>Таблица.</summary>
    Spreadsheet,

    /// <summary>Презентация.</summary>
    Presentation,

    /// <summary>PDF.</summary>
    Pdf,

    /// <summary>Архив.</summary>
    Archive,

    /// <summary>Исходный код.</summary>
    Code,

    /// <summary>Программа.</summary>
    Executable,

    /// <summary>Ярлык.</summary>
    Shortcut,
}

/// <summary>Тип совпадения термов с именем.</summary>
public enum MatchKind
{
    /// <summary>Имя совпадает с запросом.</summary>
    Exact,

    /// <summary>Имя начинается с терма.</summary>
    Prefix,

    /// <summary>Слово в имени начинается с терма.</summary>
    WordStart,

    /// <summary>Подстрока.</summary>
    Substring,

    /// <summary>Нечёткое совпадение.</summary>
    Fuzzy,

    /// <summary>Совпадения нет (подборка, фильтры).</summary>
    None,
}

/// <summary>
/// Диапазон текста для подсветки.
/// </summary>
/// <param name="Start">Начало (индекс символа UTF-16).</param>
/// <param name="Length">Длина.</param>
public sealed record TextRangeDto(int Start, int Length);

/// <summary>
/// Содержимое папки: <c>GET /api/v1/browse?path=</c> и <c>GET /api/v1/entries/{id}/children</c>.
/// </summary>
/// <param name="Path">Полный путь папки.</param>
/// <param name="RootId">Корень, которому принадлежит папка.</param>
/// <param name="Folder">Сама папка; <c>null</c> для корня.</param>
/// <param name="Items">Записи: папки, затем файлы, по имени.</param>
/// <param name="Truncated">Записей больше лимита.</param>
public sealed record BrowseResponse(string Path, long RootId, EntryDto? Folder, IReadOnlyList<EntryDto> Items, bool Truncated);

/// <summary>
/// Пакет изменений индекса: событие <c>OnEntriesChanged</c>.
/// </summary>
/// <param name="RootId">Корень.</param>
/// <param name="Changes">Изменения.</param>
public sealed record EntriesChangedDto(long RootId, IReadOnlyList<EntryChangeDto> Changes);

/// <summary>
/// Изменение записи.
/// </summary>
/// <param name="Kind">Что произошло.</param>
/// <param name="RelPath">Путь после изменения.</param>
/// <param name="EntryId">Идентификатор; <c>null</c> у удалённой записи.</param>
/// <param name="IsDirectory">Папка: изменение касается поддерева.</param>
public sealed record EntryChangeDto(EntryChangeKind Kind, string RelPath, long? EntryId, bool IsDirectory);

/// <summary>Вид изменения записи.</summary>
public enum EntryChangeKind
{
    /// <summary>Добавлена.</summary>
    Added,

    /// <summary>Изменились метаданные.</summary>
    Modified,

    /// <summary>Переименована или перенесена.</summary>
    Renamed,

    /// <summary>Удалена.</summary>
    Removed,
}

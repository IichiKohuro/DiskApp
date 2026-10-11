using DiskApp.UI.Localization;
using DiskApp.UI.Text;

namespace DiskApp.UI.Models;

/// <summary>Тег пользователя (PLAN.md, этап 8.7).</summary>
/// <param name="Id">Идентификатор.</param>
/// <param name="Name">Имя.</param>
/// <param name="Color">Цвет <c>#RRGGBB</c> или <c>null</c>.</param>
public sealed record TagInfo(long Id, string Name, string? Color)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>Тег у записи: имя и цвет для чипа.</summary>
/// <param name="Name">Имя.</param>
/// <param name="Color">Цвет <c>#RRGGBB</c> или <c>null</c> — нейтральный чип.</param>
/// <param name="Folder">
/// Папка, от которой тег достался записи (авто-тег, PLAN.md, этап 19); <c>null</c> — тег поставлен самой записи.
/// </param>
public sealed record TagLabel(string Name, string? Color, string? Folder = null)
{
    /// <summary>Тег унаследован от папки: чип рисуется контуром, снять его можно только у папки.</summary>
    public bool IsInherited => Folder is not null;

    /// <summary>Подсказка к чипу авто-тега; у своего тега — <c>null</c>.</summary>
    public string? Hint => Folder is null ? null : L.F("Tags.InheritedFrom", Folder);
}

/// <summary>Авто-теги рабочей папки (PLAN.md, этап 19): действуют на всё внутри неё, текущее и новое.</summary>
/// <param name="Path">Полный путь папки.</param>
/// <param name="Tags">Теги.</param>
public sealed record FolderTagsInfo(string Path, IReadOnlyList<TagInfo> Tags)
{
    /// <summary>
    /// Авто-теги, которые получает путь от папок с авто-тегами: ближайшая папка первой, каждый тег один раз.
    /// </summary>
    /// <param name="folders">Все папки с авто-тегами.</param>
    /// <param name="path">Файл или папка.</param>
    public static IReadOnlyList<TagLabel> InheritedAt(IEnumerable<FolderTagsInfo> folders, string path)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(path);

        var seen = new HashSet<long>();
        return [.. folders.Where(folder => PathText.IsSameOrInside(path, folder.Path))
            .OrderByDescending(static folder => folder.Path.Length)
            .SelectMany(static folder => folder.Tags.Select(tag => (Tag: tag, folder.Path)))
            .Where(pair => seen.Add(pair.Tag.Id))
            .Select(static pair => new TagLabel(pair.Tag.Name, pair.Tag.Color, pair.Path))];
    }
}

/// <summary>Вид коллекции.</summary>
public enum CollectionType
{
    /// <summary>Ручная: записи добавляются перетаскиванием или командой.</summary>
    Manual,

    /// <summary>Сохранённый запрос: состав обновляется сам.</summary>
    SavedQuery,
}

/// <summary>Коллекция (PLAN.md, этап 8.7).</summary>
/// <param name="Id">Идентификатор.</param>
/// <param name="Name">Имя.</param>
/// <param name="Type">Вид.</param>
/// <param name="Query">Строка запроса у сохранённого запроса.</param>
public sealed record CollectionInfo(long Id, string Name, CollectionType Type, string? Query);

/// <summary>Итог поиска записей по путям.</summary>
/// <param name="Entries">Найденные записи.</param>
/// <param name="Missing">Пути, которых нет в индексе или которые лежат вне корней.</param>
public sealed record ResolveResult(IReadOnlyList<FileEntry> Entries, IReadOnlyList<string> Missing);

/// <summary>
/// Цвета тегов (PLAN.md, этап 8.7): палитра под светлую и тёмную темы, как у меток Outlook и Проводника.
/// </summary>
public static class TagPalette
{
    /// <summary>Цвета палитры по порядку.</summary>
    public static IReadOnlyList<string> Colors { get; } =
    [
        "#E74856", "#F7630C", "#FFB900", "#16C60C", "#00B7C3", "#0078D4", "#8764B8", "#E3008C", "#7A7574",
    ];

    /// <summary>Цвет для нового тега: следующий по кругу после уже занятых.</summary>
    public static string Next(IEnumerable<string?> used)
    {
        ArgumentNullException.ThrowIfNull(used);

        var taken = used.Where(static color => color is not null).Select(static color => color!.ToUpperInvariant()).ToHashSet();
        return Colors.FirstOrDefault(color => !taken.Contains(color)) ?? Colors[taken.Count % Colors.Count];
    }
}

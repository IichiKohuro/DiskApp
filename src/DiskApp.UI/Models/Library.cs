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
public sealed record TagLabel(string Name, string? Color);

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

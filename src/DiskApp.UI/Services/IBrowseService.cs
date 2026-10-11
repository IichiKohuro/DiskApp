using DiskApp.UI.Models;

namespace DiskApp.UI.Services;

/// <summary>Порядок содержимого папки.</summary>
public enum BrowseSort
{
    /// <summary>По имени.</summary>
    Name,

    /// <summary>По дате изменения.</summary>
    Modified,

    /// <summary>По размеру.</summary>
    Size,

    /// <summary>По виду файла (затем по имени).</summary>
    Kind,
}

/// <summary>
/// Содержимое папки внутри корней.
/// </summary>
/// <param name="Path">Полный путь папки.</param>
/// <param name="RootId">Корень.</param>
/// <param name="Folder">Сама папка; <c>null</c> — это корень.</param>
/// <param name="Items">Папки, затем файлы.</param>
/// <param name="Truncated">Записей больше, чем вернул сервер.</param>
public sealed record FolderListing(string Path, long RootId, FileEntry? Folder, IReadOnlyList<FileEntry> Items, bool Truncated);

/// <summary>
/// Обзор папок (PLAN.md, этап 8.3): содержимое папки по пути из индекса.
/// </summary>
public interface IBrowseService
{
    /// <summary>
    /// Содержимое папки внутри добавленных корней. Сортировка по виду делается на стороне интерфейса.
    /// </summary>
    Task<FolderListing> ListAsync(string path, BrowseSort sort, bool descending, CancellationToken cancellationToken = default);
}

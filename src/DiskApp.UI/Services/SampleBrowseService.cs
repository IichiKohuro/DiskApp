using DiskApp.UI.Models;

namespace DiskApp.UI.Services;

/// <summary>
/// Обзор по макетному набору <see cref="SampleData"/>: папки восстанавливаются из путей файлов.
/// </summary>
public sealed class SampleBrowseService : IBrowseService
{
    private readonly IReadOnlyList<FileEntry> _entries;

    /// <summary>
    /// Создаёт макет относительно текущего времени.
    /// </summary>
    public SampleBrowseService()
        : this(static () => DateTimeOffset.Now)
    {
    }

    /// <summary>
    /// Создаёт макет с заданными часами (для тестов).
    /// </summary>
    public SampleBrowseService(Func<DateTimeOffset> clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _entries = SampleData.CreateEntries(clock());
    }

    /// <inheritdoc />
    public Task<FolderListing> ListAsync(string path, BrowseSort sort, bool descending, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        var folder = path.TrimEnd('\\', '/');
        var root = SampleData.Roots.FirstOrDefault(candidate => folder.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Путь «{path}» вне добавленных корней.");

        var children = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _entries)
        {
            if (!entry.FullPath.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rest = entry.FullPath[(folder.Length + 1)..];
            var slash = rest.IndexOf('\\', StringComparison.Ordinal);
            if (slash < 0)
            {
                children[entry.FullPath] = entry;
            }
            else
            {
                // Промежуточная папка без собственной записи в макете.
                var child = folder + "\\" + rest[..slash];
                children.TryAdd(child, new FileEntry { FullPath = child, Kind = FileKind.Folder, Root = root, Modified = entry.Modified });
            }
        }

        IEnumerable<FileEntry> ordered = sort switch
        {
            BrowseSort.Modified => children.Values.OrderBy(static entry => entry.Modified),
            BrowseSort.Size => children.Values.OrderBy(static entry => entry.Size),
            _ => children.Values.OrderBy(static entry => entry.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        if (descending && sort != BrowseSort.Kind)
        {
            ordered = ordered.Reverse();
        }

        var items = ordered.OrderByDescending(static entry => entry.Kind == FileKind.Folder).ToArray();
        var self = string.Equals(folder, root, StringComparison.OrdinalIgnoreCase)
            ? null
            : new FileEntry { FullPath = folder, Kind = FileKind.Folder, Root = root };
        return Task.FromResult(new FolderListing(folder, SampleData.Roots.ToList().IndexOf(root) + 1, self, items, false));
    }
}

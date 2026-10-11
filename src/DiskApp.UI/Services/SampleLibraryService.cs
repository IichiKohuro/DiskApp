using DiskApp.UI.Models;

namespace DiskApp.UI.Services;

/// <summary>
/// Избранное, теги и коллекции на макетных данных: превью дизайнера и тесты страниц (PLAN.md, этап 8.7).
/// </summary>
public sealed class SampleLibraryService : ILibraryService
{
    private readonly Lock _gate = new();
    private readonly List<FileEntry> _entries;
    private readonly List<TagInfo> _tags;
    private readonly List<long> _favorites;
    private readonly List<CollectionInfo> _collections;
    private readonly Dictionary<long, List<long>> _items = [];
    private long _nextId = 1_000;

    /// <summary>Создаёт сервис на образцах «сейчас».</summary>
    public SampleLibraryService()
        : this(static () => DateTimeOffset.Now)
    {
    }

    /// <summary>Создаёт сервис на образцах для заданного времени.</summary>
    public SampleLibraryService(Func<DateTimeOffset> clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        var entries = SampleData.CreateEntries(clock()).Select(static (entry, index) => entry with { Id = index + 1 }).ToList();
        _tags = [.. entries.SelectMany(static entry => entry.Tags).Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Select(static (name, index) => new TagInfo(index + 1, name, TagPalette.Colors[index % TagPalette.Colors.Count]))];
        _entries = [.. entries.Select(entry => entry with { TagColors = Colors(entry.Tags) })];
        _favorites = [.. _entries.Where(static entry => entry.IsFavorite).Select(static entry => entry.Id)];
        _collections =
        [
            new CollectionInfo(1, "Проект DiskApp", CollectionType.Manual, null),
            new CollectionInfo(2, "Все PDF", CollectionType.SavedQuery, "pdf"),
        ];
        _items[1] = [.. _entries.Where(static entry => entry.FullPath.Contains("DiskApp", StringComparison.OrdinalIgnoreCase)).Take(3).Select(static entry => entry.Id)];
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FileEntry>> GetFavoritesAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<FileEntry>>([.. _favorites.Select(Find).OfType<FileEntry>()]);
        }
    }

    /// <inheritdoc />
    public Task ReorderFavoritesAsync(IReadOnlyList<long> entryIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        lock (_gate)
        {
            if (entryIds.Count != _favorites.Count || !entryIds.All(_favorites.Contains))
            {
                throw new InvalidOperationException("Передайте все записи избранного по одному разу.");
            }

            _favorites.Clear();
            _favorites.AddRange(entryIds);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TagInfo>> GetTagsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<TagInfo>>([.. _tags.OrderBy(static tag => tag.Name, StringComparer.CurrentCultureIgnoreCase)]);
        }
    }

    /// <inheritdoc />
    public Task<TagInfo> CreateTagAsync(string name, string? color, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            EnsureFreeName(name, exceptId: null);
            var tag = new TagInfo(++_nextId, name.Trim(), color);
            _tags.Add(tag);
            return Task.FromResult(tag);
        }
    }

    /// <inheritdoc />
    public Task<TagInfo> UpdateTagAsync(long id, string name, string? color, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            EnsureFreeName(name, id);
            var index = _tags.FindIndex(tag => tag.Id == id);
            var old = _tags[index];
            var tag = new TagInfo(id, name.Trim(), color);
            _tags[index] = tag;
            for (var i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Tags.Contains(old.Name))
                {
                    var tags = _entries[i].Tags.Select(current => current == old.Name ? tag.Name : current).ToArray();
                    _entries[i] = _entries[i] with { Tags = tags, TagColors = Colors(tags) };
                }
            }

            return Task.FromResult(tag);
        }
    }

    /// <inheritdoc />
    public Task DeleteTagAsync(long id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var tag = _tags.Single(candidate => candidate.Id == id);
            _tags.Remove(tag);
            for (var i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Tags.Contains(tag.Name))
                {
                    var tags = _entries[i].Tags.Where(current => current != tag.Name).ToArray();
                    _entries[i] = _entries[i] with { Tags = tags, TagColors = Colors(tags) };
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<FileEntry> SetTagsAsync(FileEntry entry, IReadOnlyList<long> tagIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(tagIds);
        lock (_gate)
        {
            var names = _tags.Where(tag => tagIds.Contains(tag.Id)).Select(static tag => tag.Name).ToArray();
            var updated = entry with { Tags = names, TagColors = Colors(names) };
            var index = _entries.FindIndex(candidate => candidate.Id == entry.Id);
            if (index >= 0)
            {
                _entries[index] = _entries[index] with { Tags = names, TagColors = updated.TagColors };
            }

            return Task.FromResult(updated);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CollectionInfo>> GetCollectionsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<CollectionInfo>>([.. _collections.OrderBy(static collection => collection.Name, StringComparer.CurrentCultureIgnoreCase)]);
        }
    }

    /// <inheritdoc />
    public Task<CollectionInfo> CreateCollectionAsync(string name, CollectionType type, string? query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        lock (_gate)
        {
            var collection = new CollectionInfo(++_nextId, name.Trim(), type, type == CollectionType.SavedQuery ? query?.Trim() : null);
            _collections.Add(collection);
            _items[collection.Id] = [];
            return Task.FromResult(collection);
        }
    }

    /// <inheritdoc />
    public Task<CollectionInfo> UpdateCollectionAsync(CollectionInfo collection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        lock (_gate)
        {
            var index = _collections.FindIndex(candidate => candidate.Id == collection.Id);
            _collections[index] = collection;
            return Task.FromResult(collection);
        }
    }

    /// <inheritdoc />
    public Task DeleteCollectionAsync(long id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _collections.RemoveAll(collection => collection.Id == id);
            _items.Remove(id);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FileEntry>> GetCollectionItemsAsync(CollectionInfo collection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        lock (_gate)
        {
            if (collection.Type == CollectionType.SavedQuery)
            {
                var words = (collection.Query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return Task.FromResult<IReadOnlyList<FileEntry>>([.. _entries.Where(entry => words.Length > 0
                    && words.All(word => entry.Name.Contains(word, StringComparison.CurrentCultureIgnoreCase)))]);
            }

            return Task.FromResult<IReadOnlyList<FileEntry>>([.. (_items.GetValueOrDefault(collection.Id) ?? []).Select(Find).OfType<FileEntry>()]);
        }
    }

    /// <inheritdoc />
    public Task SetCollectionItemsAsync(long collectionId, IReadOnlyList<long> entryIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entryIds);
        lock (_gate)
        {
            _items[collectionId] = [.. entryIds.Distinct()];
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ResolveResult> ResolveAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        lock (_gate)
        {
            var found = new List<FileEntry>();
            var missing = new List<string>();
            foreach (var path in paths)
            {
                if (_entries.Find(entry => string.Equals(entry.FullPath, path, StringComparison.OrdinalIgnoreCase)) is { } entry)
                {
                    found.Add(entry);
                }
                else
                {
                    missing.Add(path);
                }
            }

            return Task.FromResult(new ResolveResult([.. found.DistinctBy(static entry => entry.Id)], missing));
        }
    }

    /// <summary>Отмечает запись избранной или нет (как это делает быстрый поиск через сервер).</summary>
    public void SetFavorite(long entryId, bool favorite)
    {
        lock (_gate)
        {
            _favorites.Remove(entryId);
            if (favorite)
            {
                _favorites.Add(entryId);
            }
        }
    }

    private FileEntry? Find(long id) => _entries.Find(entry => entry.Id == id);

    private Dictionary<string, string> Colors(IEnumerable<string> names)
        => names.Select(name => _tags.Find(tag => string.Equals(tag.Name, name, StringComparison.Ordinal))).OfType<TagInfo>()
            .Where(static tag => tag.Color is not null)
            .ToDictionary(static tag => tag.Name, static tag => tag.Color!);

    private void EnsureFreeName(string name, long? exceptId)
    {
        if (_tags.Exists(tag => tag.Id != exceptId && string.Equals(tag.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Тег «{name.Trim()}» уже есть.");
        }
    }
}

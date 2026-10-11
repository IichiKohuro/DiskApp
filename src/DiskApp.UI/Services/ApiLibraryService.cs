using System.Collections.Concurrent;

using DiskApp.Client;
using DiskApp.Contracts.Entries;
using DiskApp.Contracts.Library;
using DiskApp.Contracts.Roots;
using DiskApp.Contracts.Search;
using DiskApp.UI.Models;

using ContractCollectionKind = DiskApp.Contracts.Library.CollectionKind;

namespace DiskApp.UI.Services;

/// <summary>
/// Избранное, теги и коллекции через API (PLAN.md, этап 8.7). Состав сохранённого запроса — результаты поиска по его строке.
/// </summary>
public sealed class ApiLibraryService(IDiskAppApi api) : ILibraryService
{
    /// <summary>Сколько результатов показывать у сохранённого запроса.</summary>
    public const int SavedQueryLimit = 200;

    private readonly IDiskAppApi _api = api ?? throw new ArgumentNullException(nameof(api));
    private readonly ConcurrentDictionary<long, RootLabel> _roots = new();

    /// <inheritdoc />
    public async Task<IReadOnlyList<FileEntry>> GetFavoritesAsync(CancellationToken cancellationToken = default)
        => await ToEntriesAsync(await _api.GetFavoritesAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public Task ReorderFavoritesAsync(IReadOnlyList<long> entryIds, CancellationToken cancellationToken = default)
        => _api.ReorderFavoritesAsync(entryIds, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<TagInfo>> GetTagsAsync(CancellationToken cancellationToken = default)
        => [.. (await _api.GetTagsAsync(cancellationToken).ConfigureAwait(false))
            .Select(ToTag)
            .OrderBy(static tag => tag.Name, StringComparer.CurrentCultureIgnoreCase)];

    /// <inheritdoc />
    public async Task<TagInfo> CreateTagAsync(string name, string? color, CancellationToken cancellationToken = default)
        => ToTag(await _api.CreateTagAsync(new TagRequest { Name = name.Trim(), Color = color }, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public async Task<TagInfo> UpdateTagAsync(long id, string name, string? color, CancellationToken cancellationToken = default)
        => ToTag(await _api.UpdateTagAsync(id, new TagRequest { Name = name.Trim(), Color = color }, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public Task DeleteTagAsync(long id, CancellationToken cancellationToken = default) => _api.DeleteTagAsync(id, cancellationToken);

    /// <inheritdoc />
    public async Task<FileEntry> SetTagsAsync(FileEntry entry, IReadOnlyList<long> tagIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var updated = await _api.SetEntryTagsAsync(entry.Id, tagIds, cancellationToken).ConfigureAwait(false);
        var mapped = ApiSearchService.ToEntry(updated, entry.Root, entry.IsOffline);
        return entry with { Tags = mapped.Tags, TagColors = mapped.TagColors };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CollectionInfo>> GetCollectionsAsync(CancellationToken cancellationToken = default)
        => [.. (await _api.GetCollectionsAsync(cancellationToken).ConfigureAwait(false))
            .Select(ToCollection)
            .OrderBy(static collection => collection.Name, StringComparer.CurrentCultureIgnoreCase)];

    /// <inheritdoc />
    public async Task<CollectionInfo> CreateCollectionAsync(string name, CollectionType type, string? query, CancellationToken cancellationToken = default)
        => ToCollection(await _api.CreateCollectionAsync(Request(name, type, query), cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public async Task<CollectionInfo> UpdateCollectionAsync(CollectionInfo collection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        return ToCollection(await _api.UpdateCollectionAsync(collection.Id, Request(collection.Name, collection.Type, collection.Query), cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public Task DeleteCollectionAsync(long id, CancellationToken cancellationToken = default) => _api.DeleteCollectionAsync(id, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<FileEntry>> GetCollectionItemsAsync(CollectionInfo collection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(collection);

        var items = collection.Type == CollectionType.SavedQuery
            ? (await _api.SearchAsync(new SearchRequest { Q = collection.Query ?? string.Empty, Limit = SavedQueryLimit }, cancellationToken).ConfigureAwait(false)).Items
            : await _api.GetCollectionItemsAsync(collection.Id, cancellationToken).ConfigureAwait(false);
        return await ToEntriesAsync(items, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task SetCollectionItemsAsync(long collectionId, IReadOnlyList<long> entryIds, CancellationToken cancellationToken = default)
        => _api.SetCollectionItemsAsync(collectionId, entryIds, cancellationToken);

    /// <inheritdoc />
    public async Task<ResolveResult> ResolveAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var entries = new List<FileEntry>();
        var missing = new List<string>();
        foreach (var chunk in paths.Chunk(ResolveEntriesRequest.MaxPaths))
        {
            var response = await _api.ResolveEntriesAsync(chunk, cancellationToken).ConfigureAwait(false);
            entries.AddRange(await ToEntriesAsync(response.Entries, cancellationToken).ConfigureAwait(false));
            missing.AddRange(response.Missing);
        }

        return new ResolveResult([.. entries.DistinctBy(static entry => entry.Id)], missing);
    }

    private sealed record RootLabel(string Label, bool IsOffline);

    private static TagInfo ToTag(TagDto dto) => new(dto.Id, dto.Name, dto.Color);

    private static CollectionInfo ToCollection(CollectionDto dto)
        => new(dto.Id, dto.Name, dto.Kind == ContractCollectionKind.SavedQuery ? CollectionType.SavedQuery : CollectionType.Manual, dto.Query);

    private static CollectionRequest Request(string name, CollectionType type, string? query) => new()
    {
        Name = name.Trim(),
        Kind = type == CollectionType.SavedQuery ? ContractCollectionKind.SavedQuery : ContractCollectionKind.Manual,
        Query = type == CollectionType.SavedQuery ? query?.Trim() : null,
    };

    private async Task<IReadOnlyList<FileEntry>> ToEntriesAsync(IReadOnlyList<EntryDto> items, CancellationToken cancellationToken)
    {
        if (items.Any(item => !_roots.ContainsKey(item.RootId)))
        {
            foreach (var root in await _api.GetRootsAsync(cancellationToken).ConfigureAwait(false))
            {
                _roots[root.Id] = new RootLabel(root.Label, root.Status == RootStatus.Offline);
            }
        }

        return [.. items.Select(item => _roots.TryGetValue(item.RootId, out var root)
            ? ApiSearchService.ToEntry(item, root.Label, root.IsOffline)
            : ApiSearchService.ToEntry(item, string.Empty))];
    }
}

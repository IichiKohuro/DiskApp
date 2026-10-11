using DiskApp.Contracts.Entries;
using DiskApp.Contracts.Library;
using DiskApp.Core.Domain;
using DiskApp.Core.Paths;
using DiskApp.Core.Repositories;
using DiskApp.Server.Mapping;

namespace DiskApp.Server.Endpoints;

/// <summary>
/// Записи индекса для API: полный путь от корня, избранное, статистика открытий и теги одним набором запросов
/// на страницу записей, а не на каждую запись.
/// </summary>
internal sealed class EntryLookup(
    IRootRepository roots,
    IFileIndexRepository entries,
    IUsageRepository usage,
    IFavoriteRepository favorites,
    ITagRepository tags,
    PathNormalizer paths)
{
    /// <summary>Запись и её корень; <c>null</c>, если записи нет.</summary>
    public async Task<(FileEntry Entry, Root Root)?> FindAsync(long id, CancellationToken cancellationToken)
    {
        var entry = await entries.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return null;
        }

        var root = await roots.GetByIdAsync(entry.RootId, cancellationToken).ConfigureAwait(false);
        return root is null ? null : (entry, root);
    }

    /// <summary>Полный путь записи.</summary>
    public string FullPath(FileEntry entry, Root root) => entry.GetFullPath(root.Path, paths);

    /// <summary>Запись для API; <c>null</c>, если записи нет.</summary>
    public async Task<EntryDto?> GetAsync(long id, CancellationToken cancellationToken)
    {
        var entry = await entries.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return null;
        }

        var dtos = await ToDtosAsync([entry], cancellationToken).ConfigureAwait(false);
        return dtos.Count > 0 ? dtos[0] : null;
    }

    /// <summary>
    /// Записи для API в том же порядке; записи удалённых корней пропускаются.
    /// </summary>
    public async Task<IReadOnlyList<EntryDto>> ToDtosAsync(IReadOnlyList<FileEntry> list, CancellationToken cancellationToken)
    {
        if (list.Count == 0)
        {
            return [];
        }

        var ids = list.Select(static entry => entry.Id).ToArray();
        var rootPaths = (await roots.GetAllAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(static root => root.Id, static root => root.Path);
        var stats = (await usage.GetManyAsync(ids, cancellationToken).ConfigureAwait(false)).ToDictionary(static stat => stat.EntryId);
        var favoriteIds = (await favorites.GetAllAsync(cancellationToken).ConfigureAwait(false)).Select(static favorite => favorite.EntryId).ToHashSet();
        var tagMap = await tags.GetTagsOfEntriesAsync(ids, cancellationToken).ConfigureAwait(false);

        var result = new List<EntryDto>(list.Count);
        foreach (var entry in list)
        {
            if (!rootPaths.TryGetValue(entry.RootId, out var rootPath))
            {
                continue;
            }

            var dto = entry.ToDto(rootPath, paths, favoriteIds.Contains(entry.Id), stats.GetValueOrDefault(entry.Id));
            result.Add(tagMap.TryGetValue(entry.Id, out var entryTags) && entryTags.Count > 0 ? dto with { Tags = ToTags(entryTags) } : dto);
        }

        return result;
    }

    /// <summary>Теги для результатов поиска.</summary>
    public async Task<IReadOnlyList<EntryDto>> WithTagsAsync(IReadOnlyList<EntryDto> list, CancellationToken cancellationToken)
    {
        if (list.Count == 0)
        {
            return list;
        }

        var tagMap = await tags.GetTagsOfEntriesAsync(list.Select(static dto => dto.Id).ToArray(), cancellationToken).ConfigureAwait(false);
        return tagMap.Count == 0
            ? list
            : list.Select(dto => tagMap.TryGetValue(dto.Id, out var entryTags) && entryTags.Count > 0 ? dto with { Tags = ToTags(entryTags) } : dto).ToArray();
    }

    private static TagDto[] ToTags(IReadOnlyList<Tag> entryTags) => entryTags.Select(static tag => tag.ToDto()).ToArray();
}

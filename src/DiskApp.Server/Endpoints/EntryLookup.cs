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
    PathNormalizer paths,
    IFolderTagRepository folderTags)
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
        var inherit = await InheritorAsync(cancellationToken).ConfigureAwait(false);

        var result = new List<EntryDto>(list.Count);
        foreach (var entry in list)
        {
            if (!rootPaths.TryGetValue(entry.RootId, out var rootPath))
            {
                continue;
            }

            var dto = entry.ToDto(rootPath, paths, favoriteIds.Contains(entry.Id), stats.GetValueOrDefault(entry.Id));
            result.Add(inherit(tagMap.TryGetValue(entry.Id, out var entryTags) && entryTags.Count > 0 ? dto with { Tags = ToTags(entryTags) } : dto));
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
        var inherit = await InheritorAsync(cancellationToken).ConfigureAwait(false);
        return list.Select(dto => inherit(tagMap.TryGetValue(dto.Id, out var entryTags) && entryTags.Count > 0 ? dto with { Tags = ToTags(entryTags) } : dto)).ToArray();
    }

    private static TagDto[] ToTags(IReadOnlyList<Tag> entryTags) => entryTags.Select(static tag => tag.ToDto()).ToArray();

    /// <summary>
    /// Добавляет к записи авто-теги папок выше (этап 19): ближайшая папка первой, тег, который у записи уже есть свой или
    /// пришёл от более близкой папки, второй раз не показывается. Папок с авто-тегами десятки — они читаются один раз на ответ.
    /// </summary>
    private async Task<Func<EntryDto, EntryDto>> InheritorAsync(CancellationToken cancellationToken)
    {
        var folders = (await folderTags.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .OrderByDescending(static folder => folder.Path.Length)
            .ToArray();
        if (folders.Length == 0)
        {
            return static dto => dto;
        }

        return dto =>
        {
            HashSet<long> seen = [.. dto.Tags?.Select(static tag => tag.Id) ?? []];
            var inherited = new List<InheritedTagDto>();
            foreach (var folder in folders)
            {
                if (!paths.Comparer.Equals(dto.FullPath, folder.Path) && !paths.IsNestedIn(dto.FullPath, folder.Path))
                {
                    continue;
                }

                inherited.AddRange(folder.Tags.Where(tag => seen.Add(tag.Id)).Select(tag => new InheritedTagDto(tag.Id, tag.Name, tag.Color, folder.Path)));
            }

            return inherited.Count == 0 ? dto : dto with { InheritedTags = inherited };
        };
    }
}

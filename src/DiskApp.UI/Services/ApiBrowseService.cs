using System.Collections.Concurrent;

using DiskApp.Client;
using DiskApp.Contracts.Roots;
using DiskApp.Contracts.Search;

namespace DiskApp.UI.Services;

/// <summary>
/// <see cref="IBrowseService"/> поверх клиента API: <c>/browse</c> по пути, подписи и доступность корней кэшируются.
/// </summary>
public sealed class ApiBrowseService(IDiskAppApi api) : IBrowseService
{
    private const int PageSize = 2_000;

    private readonly IDiskAppApi _api = api ?? throw new ArgumentNullException(nameof(api));
    private readonly ConcurrentDictionary<long, (string Label, bool Offline)> _roots = new();

    /// <inheritdoc />
    public async Task<FolderListing> ListAsync(string path, BrowseSort sort, bool descending, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var serverSort = sort switch
        {
            BrowseSort.Modified => SearchSort.Modified,
            BrowseSort.Size => SearchSort.Size,
            _ => SearchSort.Name,
        };
        var response = await _api.BrowseAsync(path, new BrowseOptions(serverSort, sort == BrowseSort.Kind ? false : descending, null, PageSize), cancellationToken).ConfigureAwait(false);
        if (!_roots.ContainsKey(response.RootId))
        {
            foreach (var root in await _api.GetRootsAsync(cancellationToken).ConfigureAwait(false))
            {
                _roots[root.Id] = (root.Label, root.Status == RootStatus.Offline);
            }
        }

        var (label, offline) = _roots.GetValueOrDefault(response.RootId, (string.Empty, false));
        return new FolderListing(
            response.Path,
            response.RootId,
            response.Folder is null ? null : ApiSearchService.ToEntry(response.Folder, label, offline),
            [.. response.Items.Select(item => ApiSearchService.ToEntry(item, label, offline))],
            response.Truncated);
    }
}

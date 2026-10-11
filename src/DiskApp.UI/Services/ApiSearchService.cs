using System.Collections.Concurrent;

using DiskApp.Client;
using DiskApp.Contracts.Entries;
using DiskApp.Contracts.Roots;
using DiskApp.Contracts.Search;
using DiskApp.UI.Models;

using ContractKind = DiskApp.Contracts.Entries.FileKind;
using FileKind = DiskApp.UI.Models.FileKind;

namespace DiskApp.UI.Services;

/// <summary>
/// <see cref="ISearchService"/> поверх клиента API (PLAN.md, этап 7.1): поиск, подборка для пустого запроса
/// (избранное, недавно открытые и изменённые), состояние индекса и открытие файла через сервер — он же записывает
/// открытие и запрос в историю. Подписи корней берутся из API и кэшируются.
/// </summary>
public sealed class ApiSearchService(IDiskAppApi api) : ISearchService
{
    private const int PageSize = 50;
    private const int RecentCount = 20;

    private readonly IDiskAppApi _api = api ?? throw new ArgumentNullException(nameof(api));
    private readonly ConcurrentDictionary<long, string> _rootLabels = new();

    /// <inheritdoc />
    public async Task<SearchResult> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var response = await _api.SearchAsync(new SearchRequest { Q = query, Limit = PageSize }, cancellationToken).ConfigureAwait(false);
        var entries = await ToEntriesAsync(response.Items, cancellationToken).ConfigureAwait(false);
        return new SearchResult(entries, (int)Math.Min(response.Total, int.MaxValue), TimeSpan.FromMilliseconds(response.ElapsedMs), response.IsTotalApproximate);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FileEntry>> GetRecentAsync(CancellationToken cancellationToken)
    {
        // Пустой запрос сервер отвечает подборкой: избранное, недавно открытые, затем недавно изменённые.
        var response = await _api.SearchAsync(new SearchRequest { Q = string.Empty, Limit = RecentCount }, cancellationToken).ConfigureAwait(false);
        return await ToEntriesAsync(response.Items, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IndexStatus> GetIndexStatusAsync(CancellationToken cancellationToken)
    {
        var status = await _api.GetIndexStatusAsync(cancellationToken).ConfigureAwait(false);
        foreach (var root in status.Roots)
        {
            _rootLabels[root.RootId] = root.Label;
        }

        return new IndexStatus
        {
            TotalEntries = status.TotalEntries ?? 0,
            Roots = status.Roots.Select(static root => root.Label).ToArray(),
            IsIndexing = status.State == IndexerState.Indexing,
            LastUpdated = status.Roots.Select(static root => root.LastScanUtc).OfType<DateTimeOffset>().DefaultIfEmpty(DateTimeOffset.Now).Max(),
        };
    }

    /// <inheritdoc />
    public Task OpenAsync(FileEntry entry, string? query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _api.OpenAsync(entry.Id, query, cancellationToken);
    }

    /// <summary>
    /// Запись API в модель интерфейса.
    /// </summary>
    public static FileEntry ToEntry(EntryDto dto, string rootLabel)
    {
        ArgumentNullException.ThrowIfNull(dto);

        return new FileEntry
        {
            Id = dto.Id,
            FullPath = dto.FullPath,
            Kind = ToKind(dto.Kind),
            Root = rootLabel,
            Size = dto.Size,
            Modified = dto.ModifiedUtc.ToLocalTime(),
            IsFavorite = dto.IsFavorite,
            Tags = dto.Tags?.Select(static tag => tag.Name).ToArray() ?? [],
            IconKey = dto.IconKey,
            NameHighlights = dto.NameHighlights?.Select(static range => new HighlightRange(range.Start, range.Length)).ToArray(),
        };
    }

    private static FileKind ToKind(ContractKind kind) => kind switch
    {
        ContractKind.Folder => FileKind.Folder,
        ContractKind.Image => FileKind.Image,
        ContractKind.Video => FileKind.Video,
        ContractKind.Audio => FileKind.Audio,
        ContractKind.Document => FileKind.Document,
        ContractKind.Spreadsheet => FileKind.Spreadsheet,
        ContractKind.Presentation => FileKind.Presentation,
        ContractKind.Pdf => FileKind.Pdf,
        ContractKind.Archive => FileKind.Archive,
        ContractKind.Code => FileKind.Code,
        ContractKind.Executable => FileKind.Installer,
        _ => FileKind.Other,
    };

    private async Task<IReadOnlyList<FileEntry>> ToEntriesAsync(IReadOnlyList<EntryDto> items, CancellationToken cancellationToken)
    {
        if (items.Any(item => !_rootLabels.ContainsKey(item.RootId)))
        {
            foreach (var root in await _api.GetRootsAsync(cancellationToken).ConfigureAwait(false))
            {
                _rootLabels[root.Id] = root.Label;
            }
        }

        return items.Select(item => ToEntry(item, _rootLabels.GetValueOrDefault(item.RootId, string.Empty))).ToArray();
    }
}

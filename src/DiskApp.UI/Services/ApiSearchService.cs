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
/// (недавно открытые, закреплённые, недавно изменённые), состояние индекса и открытие файла через сервер — он же
/// записывает открытие и запрос в историю. Подписи и доступность корней берутся из API и кэшируются.
/// </summary>
public sealed class ApiSearchService(IDiskAppApi api) : ISearchService
{
    private const int PageSize = 50;
    private const int RecentCount = 20;
    private const int SectionCount = 8;

    private readonly IDiskAppApi _api = api ?? throw new ArgumentNullException(nameof(api));
    private readonly ConcurrentDictionary<long, RootInfo> _roots = new();

    /// <inheritdoc />
    public async Task<SearchResult> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var response = await _api.SearchAsync(new SearchRequest { Q = query, Limit = PageSize }, cancellationToken).ConfigureAwait(false);
        var entries = await ToEntriesAsync(response.Items, cancellationToken).ConfigureAwait(false);
        return new SearchResult(entries, (int)Math.Min(response.Total, int.MaxValue), TimeSpan.FromMilliseconds(response.ElapsedMs), response.IsTotalApproximate);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Пока через DiskApp ничего не открывали (новая установка), отдаёт подборку сервера для пустого запроса:
    /// избранное и недавно изменённые, чтобы главная страница не была пустой.
    /// </remarks>
    public async Task<IReadOnlyList<FileEntry>> GetRecentAsync(CancellationToken cancellationToken)
    {
        var recent = await _api.GetRecentAsync(RecentCount, cancellationToken).ConfigureAwait(false);
        if (recent.Count == 0)
        {
            recent = (await _api.SearchAsync(new SearchRequest { Q = string.Empty, Limit = RecentCount }, cancellationToken).ConfigureAwait(false)).Items;
        }

        return await ToEntriesAsync(recent, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ResultSection>> GetSuggestionsAsync(CancellationToken cancellationToken)
    {
        var pinned = _api.GetPinnedAsync(cancellationToken);
        var recent = _api.GetRecentAsync(SectionCount, cancellationToken);
        var modified = _api.SearchAsync(new SearchRequest { Q = string.Empty, Sort = SearchSort.Modified, Descending = true, Limit = SectionCount * 2 }, cancellationToken);
        await Task.WhenAll(pinned, recent, modified).ConfigureAwait(false);

        var modifiedFiles = (await modified.ConfigureAwait(false)).Items.Where(static item => !item.IsDirectory).Take(SectionCount).ToArray();
        (string Title, IReadOnlyList<EntryDto> Items)[] sections =
        [
            (SectionTitles.RecentlyOpened, await recent.ConfigureAwait(false)),
            (SectionTitles.Pinned, await pinned.ConfigureAwait(false)),
            (SectionTitles.RecentlyModified, modifiedFiles),
        ];

        var result = new List<ResultSection>(sections.Length);
        foreach (var (title, items) in sections)
        {
            if (items.Count > 0)
            {
                result.Add(new ResultSection(title, await ToEntriesAsync(items, cancellationToken).ConfigureAwait(false)));
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IndexStatus> GetIndexStatusAsync(CancellationToken cancellationToken)
    {
        var status = await _api.GetIndexStatusAsync(cancellationToken).ConfigureAwait(false);
        foreach (var root in status.Roots)
        {
            _roots[root.RootId] = new RootInfo(root.Label, root.Status == RootStatus.Offline);
        }

        return new IndexStatus
        {
            TotalEntries = status.TotalEntries ?? 0,
            Roots = status.Roots.Select(static root => root.Label).ToArray(),
            IsIndexing = status.State == IndexerState.Indexing,

            // Объём первого сканирования заранее неизвестен: показываем, сколько уже просмотрено.
            Progress = null,
            Scanned = status.Roots.Sum(static root => root.Progress?.Scanned ?? 0),
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
    /// <param name="dto">Запись API.</param>
    /// <param name="rootLabel">Подпись корня.</param>
    /// <param name="rootOffline">Корень сейчас недоступен.</param>
    public static FileEntry ToEntry(EntryDto dto, string rootLabel, bool rootOffline = false)
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
            IsCloudOnly = dto.IsCloudPlaceholder,
            IsOffline = rootOffline,
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
        if (items.Any(item => !_roots.ContainsKey(item.RootId)))
        {
            foreach (var root in await _api.GetRootsAsync(cancellationToken).ConfigureAwait(false))
            {
                _roots[root.Id] = new RootInfo(root.Label, root.Status == RootStatus.Offline);
            }
        }

        return items.Select(item => _roots.TryGetValue(item.RootId, out var root)
            ? ToEntry(item, root.Label, root.IsOffline)
            : ToEntry(item, string.Empty)).ToArray();
    }

    private readonly record struct RootInfo(string Label, bool IsOffline);
}

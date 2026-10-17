using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

using DiskApp.Contracts;
using DiskApp.Contracts.App;
using DiskApp.Contracts.Entries;
using DiskApp.Contracts.History;
using DiskApp.Contracts.Library;
using DiskApp.Contracts.Notes;
using DiskApp.Contracts.Operations;
using DiskApp.Contracts.Projects;
using DiskApp.Contracts.Roots;
using DiskApp.Contracts.Search;

namespace DiskApp.Client;

/// <summary>
/// <see cref="IDiskAppApi"/> на <see cref="HttpClient"/> и <c>System.Net.Http.Json</c> с <see cref="DiskAppJsonContext"/>.
/// Адрес сервера и токен подставляет обработчик из <see cref="ClientServiceCollectionExtensions.AddDiskAppClient"/>;
/// повторы (только GET) и таймауты — конвейер устойчивости там же.
/// </summary>
public sealed class HttpDiskAppApi(HttpClient http) : IDiskAppApi
{
    private const int MaxGetQueryLength = 1_500;
    private static readonly DiskAppJsonContext Json = DiskAppJsonContext.Default;

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <inheritdoc />
    public Task<HealthDto> GetHealthAsync(CancellationToken cancellationToken = default) => GetAsync(ApiRoutes.Health, Json.HealthDto, cancellationToken);

    /// <inheritdoc />
    public Task<AppInfoDto> GetAppInfoAsync(CancellationToken cancellationToken = default) => GetAsync(ApiRoutes.V1.AppInfo, Json.AppInfoDto, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<RootDto>> GetRootsAsync(CancellationToken cancellationToken = default) => GetAsync(ApiRoutes.V1.Roots, Json.IReadOnlyListRootDto, cancellationToken);

    /// <inheritdoc />
    public Task<RootDto> CreateRootAsync(CreateRootRequest request, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Post, ApiRoutes.V1.Roots, request, Json.CreateRootRequest, Json.RootDto, cancellationToken);

    /// <inheritdoc />
    public Task<RootDto> UpdateRootAsync(long id, UpdateRootRequest request, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Put, ApiRoutes.V1.Root(id), request, Json.UpdateRootRequest, Json.RootDto, cancellationToken);

    /// <inheritdoc />
    public Task DeleteRootAsync(long id, CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Delete, ApiRoutes.V1.Root(id), cancellationToken);

    /// <inheritdoc />
    public Task RescanRootAsync(long id, CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Post, ApiRoutes.V1.Root(id) + "/rescan", cancellationToken);

    /// <inheritdoc />
    public Task PauseRootAsync(long id, CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Post, ApiRoutes.V1.Root(id) + "/pause", cancellationToken);

    /// <inheritdoc />
    public Task ResumeRootAsync(long id, CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Post, ApiRoutes.V1.Root(id) + "/resume", cancellationToken);

    /// <inheritdoc />
    public Task<RootStatsDto> GetRootStatsAsync(long id, CancellationToken cancellationToken = default) => GetAsync(ApiRoutes.V1.Root(id) + "/stats", Json.RootStatsDto, cancellationToken);

    /// <inheritdoc />
    public Task<IndexStatusDto> GetIndexStatusAsync(CancellationToken cancellationToken = default) => GetAsync(ApiRoutes.V1.IndexStatus, Json.IndexStatusDto, cancellationToken);

    /// <inheritdoc />
    public Task<ExclusionPreviewDto> PreviewExclusionAsync(string pattern, CancellationToken cancellationToken = default)
        => GetAsync(ApiRoutes.V1.IndexExclusionPreview + new QueryBuilder().Add("pattern", pattern), Json.ExclusionPreviewDto, cancellationToken);

    /// <inheritdoc />
    public Task RebuildIndexAsync(CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Post, ApiRoutes.V1.IndexRebuild, cancellationToken);

    /// <inheritdoc />
    public Task<IndexStatusDto> PauseIndexingAsync(CancellationToken cancellationToken = default) => PostForAsync(ApiRoutes.V1.IndexPause, Json.IndexStatusDto, cancellationToken);

    /// <inheritdoc />
    public Task<IndexStatusDto> ResumeIndexingAsync(CancellationToken cancellationToken = default) => PostForAsync(ApiRoutes.V1.IndexResume, Json.IndexStatusDto, cancellationToken);

    /// <inheritdoc />
    public Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Обычный запрос — GET: он идемпотентен, и его можно повторить при сбое; очень длинный — POST.
        var query = new QueryBuilder()
            .Add("q", request.Q)
            .Add("limit", request.Limit)
            .Add("cursor", request.Cursor)
            .Add("sort", request.Sort == SearchSort.Relevance ? null : request.Sort.ToString())
            .Add("desc", request.Descending ? null : "false")
            .Add("preset", request.Preset?.ToString())
            .Add("explain", request.Explain ? "true" : null)
            .ToString();
        return query.Length <= MaxGetQueryLength
            ? GetAsync(ApiRoutes.V1.Search + query, Json.SearchResponse, cancellationToken)
            : SendAsync(HttpMethod.Post, ApiRoutes.V1.Search, request, Json.SearchRequest, Json.SearchResponse, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SuggestResponse> SuggestAsync(string? query, int? limit = null, CancellationToken cancellationToken = default)
        => GetAsync(ApiRoutes.V1.Suggest + new QueryBuilder().Add("q", query).Add("limit", limit), Json.SuggestResponse, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<HistoryEntryDto>> GetHistoryAsync(int? limit = null, CancellationToken cancellationToken = default)
        => GetAsync(ApiRoutes.V1.History + new QueryBuilder().Add("limit", limit), Json.IReadOnlyListHistoryEntryDto, cancellationToken);

    /// <inheritdoc />
    public Task DeleteHistoryEntryAsync(string query, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Delete, ApiRoutes.V1.HistoryEntry + new QueryBuilder().Add("query", query), cancellationToken);

    /// <inheritdoc />
    public Task ClearHistoryAsync(CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Delete, ApiRoutes.V1.History, cancellationToken);

    /// <inheritdoc />
    public Task<EntryDto> GetEntryAsync(long id, CancellationToken cancellationToken = default) => GetAsync(ApiRoutes.V1.Entry(id), Json.EntryDto, cancellationToken);

    /// <inheritdoc />
    public Task<BrowseResponse> GetChildrenAsync(long id, BrowseOptions? options = null, CancellationToken cancellationToken = default)
        => GetAsync(ApiRoutes.V1.Entry(id) + "/children" + Paging(new QueryBuilder(), options), Json.BrowseResponse, cancellationToken);

    /// <inheritdoc />
    public Task<BrowseResponse> BrowseAsync(string path, BrowseOptions? options = null, CancellationToken cancellationToken = default)
        => GetAsync(ApiRoutes.V1.Browse + Paging(new QueryBuilder().Add("path", path), options), Json.BrowseResponse, cancellationToken);

    /// <inheritdoc />
    public async Task DownloadContentAsync(long id, Stream destination, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);

        using var response = await _http.GetAsync(Relative(ApiRoutes.V1.Entry(id) + "/content"), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            var buffer = new byte[81_920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                total += read;
                progress?.Report(total);
            }
        }
    }

    /// <inheritdoc />
    public Task<byte[]> GetIconAsync(long id, int size = 32, CancellationToken cancellationToken = default)
        => GetBytesAsync(ApiRoutes.V1.Entry(id) + "/icon" + new QueryBuilder().Add("size", size), cancellationToken);

    /// <inheritdoc />
    public Task<byte[]> GetThumbnailAsync(long id, int size = 256, CancellationToken cancellationToken = default)
        => GetBytesAsync(ApiRoutes.V1.Entry(id) + "/thumbnail" + new QueryBuilder().Add("size", size), cancellationToken);

    /// <inheritdoc />
    public Task OpenAsync(long id, string? query = null, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Post, ApiRoutes.V1.Entry(id) + "/open" + new QueryBuilder().Add("query", query), cancellationToken);

    /// <inheritdoc />
    public Task OpenWithAsync(long id, CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Post, ApiRoutes.V1.Entry(id) + "/open-with", cancellationToken);

    /// <inheritdoc />
    public Task RevealAsync(long id, CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Post, ApiRoutes.V1.Entry(id) + "/reveal", cancellationToken);

    /// <inheritdoc />
    public Task ShowPropertiesAsync(long id, CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Post, ApiRoutes.V1.Entry(id) + "/properties", cancellationToken);

    /// <inheritdoc />
    public Task RunAsAdminAsync(long id, CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Post, ApiRoutes.V1.Entry(id) + "/run-as-admin", cancellationToken);

    /// <inheritdoc />
    public Task<EntryDto> SetEntryTagsAsync(long id, IReadOnlyList<long> tagIds, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Post, ApiRoutes.V1.Entry(id) + "/tags", new EntryTagsRequest { TagIds = tagIds }, Json.EntryTagsRequest, Json.EntryDto, cancellationToken);

    /// <inheritdoc />
    public Task<ResolveEntriesResponse> ResolveEntriesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Post, ApiRoutes.V1.ResolveEntries, new ResolveEntriesRequest { Paths = paths }, Json.ResolveEntriesRequest, Json.ResolveEntriesResponse, cancellationToken);

    /// <inheritdoc />
    public Task<OperationDto> EnqueueOperationAsync(OperationRequest request, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Post, ApiRoutes.V1.Operations, request, Json.OperationRequest, Json.OperationDto, cancellationToken);

    /// <inheritdoc />
    public Task<OperationDto> SortFilesAsync(SortFilesRequest request, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Post, ApiRoutes.V1.SortingMove, request, Json.SortFilesRequest, Json.OperationDto, cancellationToken);

    /// <inheritdoc />
    public Task<OperationDto> DropFilesAsync(DropFilesRequest request, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Post, ApiRoutes.V1.SortingDrop, request, Json.DropFilesRequest, Json.OperationDto, cancellationToken);

    /// <inheritdoc />
    public Task<InboxDto> GetInboxAsync(CancellationToken cancellationToken = default) => GetAsync(ApiRoutes.V1.SortingInbox, Json.InboxDto, cancellationToken);

    /// <inheritdoc />
    public async Task SetInboxStateAsync(SetInboxStateRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PutAsJsonAsync(Relative(ApiRoutes.V1.SortingInboxState), request, Json.SetInboxStateRequest, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<SortingPreviewDto> PreviewSortingRulesAsync(SortingPreviewRequest request, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Post, ApiRoutes.V1.SortingRulesPreview, request, Json.SortingPreviewRequest, Json.SortingPreviewDto, cancellationToken);

    /// <inheritdoc />
    public Task<FileHistoryPageDto> GetFileHistoryAsync(FileHistoryFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var query = new List<string>();
        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                query.Add(name + "=" + Uri.EscapeDataString(value));
            }
        }

        Add("folder", filter.Folder);
        Add("q", filter.Text);
        Add("kinds", filter.Kinds.Count == 0 ? null : string.Join(',', filter.Kinds));
        Add("sources", filter.Sources.Count == 0 ? null : string.Join(',', filter.Sources));
        Add("from", filter.FromUtc?.ToString("O", CultureInfo.InvariantCulture));
        Add("to", filter.ToUtc?.ToString("O", CultureInfo.InvariantCulture));
        Add("before", filter.Before?.ToString(CultureInfo.InvariantCulture));
        Add("limit", filter.Limit.ToString(CultureInfo.InvariantCulture));
        return GetAsync(ApiRoutes.V1.FileHistory + (query.Count == 0 ? string.Empty : "?" + string.Join('&', query)), Json.FileHistoryPageDto, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FileEventDto>> GetFileHistoryOfPathAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return GetAsync(ApiRoutes.V1.FileHistoryOfPath + "?path=" + Uri.EscapeDataString(path), Json.IReadOnlyListFileEventDto, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<FileHistoryRevertDto> RevertFileEventAsync(long id, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(Relative(ApiRoutes.V1.FileHistoryRevertOf(id)), content: null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync(Json.FileHistoryRevertDto, cancellationToken).ConfigureAwait(false))!;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<OperationDto>> GetOperationsAsync(CancellationToken cancellationToken = default)
        => GetAsync(ApiRoutes.V1.Operations, Json.IReadOnlyListOperationDto, cancellationToken);

    /// <inheritdoc />
    public Task<OperationDto> GetOperationAsync(Guid id, CancellationToken cancellationToken = default) => GetAsync(ApiRoutes.V1.Operation(id), Json.OperationDto, cancellationToken);

    /// <inheritdoc />
    public Task CancelOperationAsync(Guid id, CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Post, ApiRoutes.V1.Operation(id) + "/cancel", cancellationToken);

    /// <inheritdoc />
    public async Task DecideOperationAsync(Guid id, ConflictDecisionDto decision, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Relative(ApiRoutes.V1.Operation(id) + "/decision")) { Content = JsonContent.Create(decision, Json.ConflictDecisionDto) };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<OperationDto> UndoOperationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(Relative(ApiRoutes.V1.Operation(id) + "/undo"), null, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, Json.OperationDto, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<UploadResponse> UploadAsync(string destination, IReadOnlyList<UploadFile> files, ConflictPolicy conflicts = ConflictPolicy.KeepBoth,
        IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);

        long sent = 0;
        using var content = new MultipartFormDataContent();
        foreach (var file in files)
        {
            var stream = new ProgressStream(file.OpenRead(), read => progress?.Report(Interlocked.Add(ref sent, read)));
            var part = new StreamContent(stream, 81_920);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(part, "files", file.FileName);
        }

        var uri = ApiRoutes.V1.Upload + new QueryBuilder().Add("destination", destination).Add("conflicts", conflicts.ToString());
        using var response = await _http.PostAsync(Relative(uri), content, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, Json.UploadResponse, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<EntryDto>> GetFavoritesAsync(CancellationToken cancellationToken = default) => GetAsync(ApiRoutes.V1.Favorites, Json.IReadOnlyListEntryDto, cancellationToken);

    /// <inheritdoc />
    public Task<EntryDto> AddFavoriteAsync(long entryId, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Post, ApiRoutes.V1.Favorites, new FavoriteRequest { EntryId = entryId }, Json.FavoriteRequest, Json.EntryDto, cancellationToken);

    /// <inheritdoc />
    public Task RemoveFavoriteAsync(long entryId, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Delete, ApiRoutes.V1.Favorites + "/" + entryId.ToString(CultureInfo.InvariantCulture), cancellationToken);

    /// <inheritdoc />
    public async Task ReorderFavoritesAsync(IReadOnlyList<long> entryIds, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PutAsJsonAsync(Relative(ApiRoutes.V1.FavoritesOrder), new FavoritesOrderRequest { EntryIds = entryIds }, Json.FavoritesOrderRequest, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<EntryDto>> GetPinnedAsync(CancellationToken cancellationToken = default) => GetAsync(ApiRoutes.V1.Pinned, Json.IReadOnlyListEntryDto, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<EntryDto>> GetRecentAsync(int? limit = null, CancellationToken cancellationToken = default)
        => GetAsync(ApiRoutes.V1.Recent + new QueryBuilder().Add("limit", limit), Json.IReadOnlyListEntryDto, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<TagDto>> GetTagsAsync(CancellationToken cancellationToken = default) => GetAsync(ApiRoutes.V1.Tags, Json.IReadOnlyListTagDto, cancellationToken);

    /// <inheritdoc />
    public Task<TagDto> CreateTagAsync(TagRequest request, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Post, ApiRoutes.V1.Tags, request, Json.TagRequest, Json.TagDto, cancellationToken);

    /// <inheritdoc />
    public Task<TagDto> UpdateTagAsync(long id, TagRequest request, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Put, ApiRoutes.V1.Tag(id), request, Json.TagRequest, Json.TagDto, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<FolderTagsDto>> GetAutoTagsAsync(CancellationToken cancellationToken = default)
        => GetAsync(ApiRoutes.V1.AutoTags, Json.IReadOnlyListFolderTagsDto, cancellationToken);

    /// <inheritdoc />
    public Task<FolderTagsDto> SetAutoTagsAsync(SetFolderTagsRequest request, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Put, ApiRoutes.V1.AutoTags, request, Json.SetFolderTagsRequest, Json.FolderTagsDto, cancellationToken);

    /// <inheritdoc />
    public Task DeleteTagAsync(long id, CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Delete, ApiRoutes.V1.Tag(id), cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<NoteDto>> GetNotesAsync(string? query = null, long? tagId = null, bool deleted = false, long? projectId = null, CancellationToken cancellationToken = default)
        => GetAsync(
            ApiRoutes.V1.Notes + new QueryBuilder()
                .Add("q", string.IsNullOrWhiteSpace(query) ? null : query)
                .Add("tagId", tagId?.ToString(CultureInfo.InvariantCulture))
                .Add("projectId", projectId?.ToString(CultureInfo.InvariantCulture))
                .Add("deleted", deleted ? "true" : null),
            Json.IReadOnlyListNoteDto,
            cancellationToken);

    /// <inheritdoc />
    public Task<NoteDto> GetNoteAsync(long id, CancellationToken cancellationToken = default) => GetAsync(ApiRoutes.V1.Note(id), Json.NoteDto, cancellationToken);

    /// <inheritdoc />
    public Task<NoteDto> CreateNoteAsync(NoteRequest request, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Post, ApiRoutes.V1.Notes, request, Json.NoteRequest, Json.NoteDto, cancellationToken);

    /// <inheritdoc />
    public Task<NoteDto> UpdateNoteAsync(long id, NoteRequest request, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Put, ApiRoutes.V1.Note(id), request, Json.NoteRequest, Json.NoteDto, cancellationToken);

    /// <inheritdoc />
    public Task DeleteNoteAsync(long id, bool permanent = false, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Delete, ApiRoutes.V1.Note(id) + new QueryBuilder().Add("permanent", permanent ? "true" : null), cancellationToken);

    /// <inheritdoc />
    public Task<NoteDto> RestoreNoteAsync(long id, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Post, ApiRoutes.V1.Note(id) + "/restore", new NoteRequest(), Json.NoteRequest, Json.NoteDto, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<TagDto>> GetNoteTagsAsync(CancellationToken cancellationToken = default) => GetAsync(ApiRoutes.V1.NoteTags, Json.IReadOnlyListTagDto, cancellationToken);

    /// <inheritdoc />
    public Task<TagDto> CreateNoteTagAsync(TagRequest request, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Post, ApiRoutes.V1.NoteTags, request, Json.TagRequest, Json.TagDto, cancellationToken);

    /// <inheritdoc />
    public Task<TagDto> UpdateNoteTagAsync(long id, TagRequest request, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Put, ApiRoutes.V1.NoteTag(id), request, Json.TagRequest, Json.TagDto, cancellationToken);

    /// <inheritdoc />
    public Task DeleteNoteTagAsync(long id, CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Delete, ApiRoutes.V1.NoteTag(id), cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<ProjectDto>> GetProjectsAsync(CancellationToken cancellationToken = default)
        => GetAsync(ApiRoutes.V1.Projects, Json.IReadOnlyListProjectDto, cancellationToken);

    /// <inheritdoc />
    public Task<ProjectDto> CreateProjectAsync(ProjectRequest request, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Post, ApiRoutes.V1.Projects, request, Json.ProjectRequest, Json.ProjectDto, cancellationToken);

    /// <inheritdoc />
    public Task<ProjectDto> UpdateProjectAsync(long id, ProjectRequest request, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Put, ApiRoutes.V1.Project(id), request, Json.ProjectRequest, Json.ProjectDto, cancellationToken);

    /// <inheritdoc />
    public Task DeleteProjectAsync(long id, CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Delete, ApiRoutes.V1.Project(id), cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<CollectionDto>> GetCollectionsAsync(CancellationToken cancellationToken = default)
        => GetAsync(ApiRoutes.V1.Collections, Json.IReadOnlyListCollectionDto, cancellationToken);

    /// <inheritdoc />
    public Task<CollectionDto> CreateCollectionAsync(CollectionRequest request, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Post, ApiRoutes.V1.Collections, request, Json.CollectionRequest, Json.CollectionDto, cancellationToken);

    /// <inheritdoc />
    public Task<CollectionDto> UpdateCollectionAsync(long id, CollectionRequest request, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Put, ApiRoutes.V1.Collection(id), request, Json.CollectionRequest, Json.CollectionDto, cancellationToken);

    /// <inheritdoc />
    public Task DeleteCollectionAsync(long id, CancellationToken cancellationToken = default) => SendAsync(HttpMethod.Delete, ApiRoutes.V1.Collection(id), cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<EntryDto>> GetCollectionItemsAsync(long id, CancellationToken cancellationToken = default)
        => GetAsync(ApiRoutes.V1.Collection(id) + "/items", Json.IReadOnlyListEntryDto, cancellationToken);

    /// <inheritdoc />
    public async Task SetCollectionItemsAsync(long id, IReadOnlyList<long> entryIds, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PutAsJsonAsync(Relative(ApiRoutes.V1.Collection(id) + "/items"), new CollectionItemsRequest { EntryIds = entryIds }, Json.CollectionItemsRequest, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<SettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) => GetAsync(ApiRoutes.V1.Settings, Json.SettingsDto, cancellationToken);

    /// <inheritdoc />
    public Task<SettingsDto> PatchSettingsAsync(JsonObject patch, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Patch, ApiRoutes.V1.Settings, patch, Json.JsonObject, Json.SettingsDto, cancellationToken);

    /// <summary>
    /// Ошибка сервера → <see cref="DiskAppApiException"/> с содержимым ProblemDetails.
    /// </summary>
    internal static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        ApiProblem? problem = null;
        if (response.Content.Headers.ContentType?.MediaType is "application/problem+json" or "application/json")
        {
            try
            {
                problem = await response.Content.ReadFromJsonAsync(ClientJsonContext.Default.ApiProblem, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                // Тело не в формате ProblemDetails — останется код ответа.
            }
        }

        throw new DiskAppApiException(response.StatusCode, problem?.Title ?? response.ReasonPhrase, problem?.Detail, problem?.Code, problem?.Errors);
    }

    private static Uri Relative(string uri) => new(uri, UriKind.Relative);

    private static string Paging(QueryBuilder query, BrowseOptions? options)
        => options is null
            ? query.ToString()
            : query.Add("sort", options.Sort?.ToString()).Add("desc", options.Descending is { } desc ? (desc ? "true" : "false") : null).Add("offset", options.Offset).Add("limit", options.Limit).ToString();

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync(type, cancellationToken).ConfigureAwait(false)
            ?? throw new DiskAppApiException("Сервер вернул пустой ответ.");
    }

    private async Task<T> GetAsync<T>(string uri, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(Relative(uri), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, type, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> PostForAsync<T>(string uri, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync(Relative(uri), null, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, type, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> GetBytesAsync(string uri, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(Relative(uri), cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse> SendAsync<TRequest, TResponse>(HttpMethod method, string uri, TRequest body, JsonTypeInfo<TRequest> requestType, JsonTypeInfo<TResponse> responseType,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, Relative(uri)) { Content = JsonContent.Create(body, requestType) };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, responseType, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendAsync(HttpMethod method, string uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, Relative(uri));
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Строка запроса: пустые значения пропускаются, остальные кодируются.
    /// </summary>
    private sealed class QueryBuilder
    {
        private readonly StringBuilder _builder = new();

        public QueryBuilder Add(string name, string? value)
        {
            if (value is null)
            {
                return this;
            }

            _builder.Append(_builder.Length == 0 ? '?' : '&').Append(name).Append('=').Append(Uri.EscapeDataString(value));
            return this;
        }

        public QueryBuilder Add(string name, int? value) => Add(name, value?.ToString(CultureInfo.InvariantCulture));

        public override string ToString() => _builder.ToString();
    }

    /// <summary>
    /// Поток только для чтения, сообщающий о прочитанных байтах (прогресс загрузки).
    /// </summary>
    private sealed class ProgressStream(Stream inner, Action<int> onRead) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            onRead(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            onRead(read);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

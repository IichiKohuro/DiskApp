using DiskApp.Contracts;
using DiskApp.Contracts.Search;
using DiskApp.Core.Search;
using DiskApp.Server.Localization;
using DiskApp.Server.Mapping;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

using CoreSearchRequest = DiskApp.Core.Search.SearchRequest;
using RankingPreset = DiskApp.Contracts.Search.RankingPreset;
using SearchRequest = DiskApp.Contracts.Search.SearchRequest;

namespace DiskApp.Server.Endpoints;

/// <summary>
/// Поиск, подсказки и история запросов (PLAN.md, этап 4.3).
/// </summary>
internal static class SearchEndpoints
{
    public const int MaxLimit = 500;

    public static void MapSearch(this IEndpointRouteBuilder endpoints)
    {
        var search = endpoints.MapGroup(string.Empty).WithTags("Search");

        search.MapGet(ApiRoutes.V1.Search, GetAsync)
            .WithName("Search")
            .WithSummary("Поиск")
            .WithDescription("Параметры: q — строка с фильтрами (ext:, type:, in:, size:, modified: и др.), limit — 1…500, cursor — из прошлого ответа, "
                + "sort — Relevance, Name, Modified, Created, Size; desc; preset — Balanced, ByName, Recent; explain — объяснение оценки. "
                + "Пустой q — подборка: избранное, недавние и недавно изменённые.");

        search.MapPost(ApiRoutes.V1.Search, PostAsync)
            .WithName("SearchWithBody")
            .WithSummary("Поиск с телом запроса")
            .WithDescription("То же, что GET, для длинных запросов.");

        search.MapGet(ApiRoutes.V1.Suggest, SuggestAsync)
            .WithName("Suggest")
            .WithSummary("Подсказки к запросу")
            .WithDescription("Запросы из истории, ключи фильтров и их значения: частые расширения из индекса, теги, имена папок.");

        search.MapGet(ApiRoutes.V1.History, GetHistoryAsync)
            .WithName("GetHistory")
            .WithSummary("История запросов")
            .WithDescription("Последние запросы, новые первыми; limit — до 1 000.");

        search.MapDelete(ApiRoutes.V1.HistoryEntry, DeleteHistoryEntryAsync)
            .WithName("DeleteHistoryEntry")
            .WithSummary("Удалить запрос из истории")
            .WithDescription("Параметр query — текст запроса, как он записан в истории.");

        search.MapDelete(ApiRoutes.V1.History, ClearHistoryAsync)
            .WithName("ClearHistory")
            .WithSummary("Очистить историю запросов");
    }

    private static Task<Results<Ok<SearchResponse>, ValidationProblem>> GetAsync(
        string? q,
        int? limit,
        string? cursor,
        SearchSort? sort,
        bool? desc,
        RankingPreset? preset,
        bool? explain,
        SearchService service,
        EntryLookup lookup,
        CancellationToken cancellationToken)
        => RunAsync(
            new SearchRequest
            {
                Q = q,
                Limit = limit ?? 50,
                Cursor = cursor,
                Sort = sort ?? SearchSort.Relevance,
                Descending = desc ?? true,
                Preset = preset,
                Explain = explain ?? false,
            },
            service,
            lookup,
            cancellationToken);

    private static Task<Results<Ok<SearchResponse>, ValidationProblem>> PostAsync(
        SearchRequest request,
        SearchService service,
        EntryLookup lookup,
        CancellationToken cancellationToken)
        => RunAsync(request, service, lookup, cancellationToken);

    private static async Task<Results<Ok<SearchResponse>, ValidationProblem>> RunAsync(
        SearchRequest request,
        SearchService service,
        EntryLookup lookup,
        CancellationToken cancellationToken)
    {
        if (request.Limit is < 1 or > MaxLimit)
        {
            return ApiProblems.Invalid("limit", L.F("SearchEndpoints.PageSizeIsFrom1To", MaxLimit));
        }

        if (request.Q is { Length: > SearchRequest.MaxQueryLength })
        {
            return ApiProblems.Invalid("q", L.F("SearchEndpoints.QueryIsLongerThan", SearchRequest.MaxQueryLength));
        }

        SearchCursor? cursor = null;
        if (!string.IsNullOrEmpty(request.Cursor))
        {
            if (!SearchCursorCodec.TryDecode(request.Cursor, out cursor))
            {
                return ApiProblems.Invalid("cursor", L.T("SearchEndpoints.TheCursorWasNotRecognizedTake"));
            }
        }

        var page = await service.SearchAsync(
            new CoreSearchRequest
            {
                Text = request.Q,
                Limit = request.Limit,
                Cursor = cursor,
                Sort = request.Sort.ToDomain(),
                Descending = request.Descending,
                Preset = request.Preset?.ToDomain(),
                IncludeRankReason = request.Explain,
            },
            cancellationToken).ConfigureAwait(false);

        var items = await lookup.WithTagsAsync(page.Hits.Select(static hit => hit.ToDto()).ToArray(), cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new SearchResponse(
            items,
            page.TotalCount,
            page.IsTotalApproximate,
            page.NextCursor is { } next ? SearchCursorCodec.Encode(next) : null,
            Math.Round(page.Elapsed.TotalMilliseconds, 2),
            page.IsPartial,
            page.IncludesFuzzy,
            page.Warnings,
            page.ParsedQuery?.Tokens.Select(static token => token.ToDto()).ToArray() ?? []));
    }

    private static async Task<Results<Ok<SuggestResponse>, ValidationProblem>> SuggestAsync(string? q, int? limit, SearchService service, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 50)
        {
            return ApiProblems.Invalid("limit", L.T("SearchEndpoints.SuggestionsFrom1To50"));
        }

        if (q is { Length: > SearchRequest.MaxQueryLength })
        {
            return ApiProblems.Invalid("q", L.F("SearchEndpoints.QueryIsLongerThan", SearchRequest.MaxQueryLength));
        }

        var suggestions = await service.SuggestAsync(q, limit ?? 10, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new SuggestResponse(suggestions.Select(static suggestion => suggestion.ToDto()).ToArray()));
    }

    private static async Task<Results<Ok<HistoryEntryDto[]>, ValidationProblem>> GetHistoryAsync(int? limit, SearchService service, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 1_000)
        {
            return ApiProblems.Invalid("limit", L.T("SearchEndpoints.EntriesFrom1To1000"));
        }

        var history = await service.GetHistoryAsync(limit ?? 100, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(history.Select(static entry => entry.ToDto()).ToArray());
    }

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> DeleteHistoryEntryAsync(string? query, SearchService service, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return ApiProblems.Invalid("query", L.T("SearchEndpoints.SpecifyAQuery"));
        }

        return await service.RemoveFromHistoryAsync(query, cancellationToken).ConfigureAwait(false)
            ? TypedResults.NoContent()
            : ApiProblems.NotFound(L.T("SearchEndpoints.ThisQueryIsNotInThe"));
    }

    private static async Task<NoContent> ClearHistoryAsync(SearchService service, CancellationToken cancellationToken)
    {
        await service.ClearHistoryAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.NoContent();
    }
}

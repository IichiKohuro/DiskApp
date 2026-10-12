using System.Net;

using DiskApp.Contracts;
using DiskApp.Contracts.Operations;
using DiskApp.Core.Abstractions;
using DiskApp.Core.Paths;
using DiskApp.Core.Settings;
using DiskApp.Core.Sorting;
using DiskApp.Server.Localization;
using DiskApp.Server.Mapping;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace DiskApp.Server.Endpoints;

/// <summary>
/// Разбор входящих (PLAN.md, этап 17): перенос из накопителя в рабочую папку, при необходимости в новую подпапку,
/// с тегами, — все пути только внутри добавленных корней (403 иначе). Бросок в накопитель из окна Drag&amp;Drop (этап 18.2) —
/// источники и вне корней, назначение — только накопитель. Перенос и копия — операции общей очереди.
/// </summary>
internal static class SortingEndpoints
{
    public static void MapSorting(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(ApiRoutes.V1.SortingMove, MoveAsync)
            .WithTags("Sorting")
            .WithName("SortFiles")
            .WithSummary("Перенести из накопителя в рабочую папку с тегами")
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapPost(ApiRoutes.V1.SortingDrop, DropAsync)
            .WithTags("Sorting")
            .WithName("DropFiles")
            .WithSummary("Бросить файлы в папку-накопитель: перенос или копия, источники — в том числе вне корней")
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapGet(ApiRoutes.V1.SortingInbox, GetInboxAsync)
            .WithTags("Sorting")
            .WithName("GetInbox")
            .WithSummary("Очередь папок-накопителей")
            .WithDescription("Файлы новые сверху; ещё дописываемые — с isReady = false и временем, когда придут; временные, служебные и скрытые не входят. newCount — сколько ждут разбора.");

        endpoints.MapPut(ApiRoutes.V1.SortingInboxState, SetInboxStateAsync)
            .WithTags("Sorting")
            .WithName("SetInboxState")
            .WithSummary("Отложить, оставить здесь или вернуть в новые файлы накопителя");
    }

    private static async Task<Ok<InboxDto>> GetInboxAsync(InboxService inbox, EntryLookup lookup, CancellationToken cancellationToken)
    {
        var snapshot = await inbox.GetAsync(cancellationToken).ConfigureAwait(false);
        var dtos = await lookup.ToDtosAsync([.. snapshot.Items.Select(static item => item.Entry)], cancellationToken).ConfigureAwait(false);
        var byId = dtos.ToDictionary(static dto => dto.Id);
        var items = snapshot.Items
            .Where(item => byId.ContainsKey(item.Entry.Id))
            .Select(item => new InboxItemDto(byId[item.Entry.Id], item.Inbox, (InboxState)(int)item.State, snapshot.IsReady(item), item.ReadyUtc))
            .ToArray();
        var folders = snapshot.Folders.Select(static folder => new InboxFolderDto(folder.Path, folder.RootId, (Contracts.Operations.InboxProblem)(int)folder.Problem)).ToArray();
        return TypedResults.Ok(new InboxDto(folders, items, snapshot.NewCount, snapshot.NextReadyUtc, snapshot.IsTruncated));
    }

    private static async Task<Results<NoContent, ValidationProblem>> SetInboxStateAsync(SetInboxStateRequest request, InboxService inbox, CancellationToken cancellationToken)
    {
        if (request.Paths.Count is 0 or > SetInboxStateRequest.MaxPaths)
        {
            return ApiProblems.Invalid(nameof(request.Paths), L.F("SortingEndpoints.TooManyPaths", SetInboxStateRequest.MaxPaths));
        }

        if (!Enum.IsDefined(request.State))
        {
            return ApiProblems.Invalid(nameof(request.State), L.T("SortingEndpoints.UnknownState"));
        }

        return await inbox.SetStateAsync(request.Paths, (InboxItemState)(int)request.State, cancellationToken).ConfigureAwait(false)
            ? TypedResults.NoContent()
            : ApiProblems.Invalid(nameof(request.Paths), L.T("SortingEndpoints.PathsOutsideInboxes"));
    }

    private static async Task<Results<Accepted<OperationDto>, ValidationProblem, ProblemHttpResult>> MoveAsync(
        SortFilesRequest request,
        SortingService sorting,
        RootGuard guard,
        CancellationToken cancellationToken)
    {
        var sources = new List<string>(request.Sources.Count);
        foreach (var path in request.Sources.Append(request.Destination))
        {
            var (allowed, normalized, _) = await guard.CheckAsync(path, cancellationToken).ConfigureAwait(false);
            if (!allowed)
            {
                return normalized is null
                    ? ApiProblems.Invalid("sources", L.F("OperationEndpoints.PathMustBeAbsolute", path))
                    : TypedResults.Problem(L.F("OperationEndpoints.PathIsOutsideTheAddedRoots", normalized), statusCode: StatusCodes.Status403Forbidden, title: L.T("Operations.AccessDenied"));
            }

            sources.Add(normalized!);
        }

        var destination = sources[^1];
        sources.RemoveAt(sources.Count - 1);
        if (!Directory.Exists(destination))
        {
            return ApiProblems.NotFound(L.F("OperationEndpoints.FolderNotFound", destination));
        }

        var newFolder = string.IsNullOrWhiteSpace(request.NewFolder) ? null : request.NewFolder;
        var result = await sorting.MoveAsync(new SortRequest(sources, destination, newFolder, request.Tags ?? [], request.Names), cancellationToken).ConfigureAwait(false);
        if (result.TryGetValue(out var info))
        {
            return TypedResults.Accepted(ApiRoutes.V1.Operation(info.Id), info.ToDto());
        }

        return result.Error!.Code == SortingService.InvalidCode
            ? ApiProblems.Invalid("request", result.Error.Message)
            : ApiProblems.Domain(result.Error);
    }

    /// <summary>
    /// Бросок в папку-накопитель из окна Drag&amp;Drop (этап 18.2, ADR-0011). В отличие от разбора источники могут лежать вне
    /// корней — файл с рабочего стола или из мессенджера, — поэтому правило строже по остальному: запрос только с этого
    /// компьютера, назначение — накопитель из настроек внутри корней, источники — существующие абсолютные пути.
    /// </summary>
    private static async Task<Results<Accepted<OperationDto>, ValidationProblem, ProblemHttpResult>> DropAsync(
        DropFilesRequest request,
        HttpContext http,
        SortingService sorting,
        RootGuard guard,
        PathNormalizer paths,
        ISettingsStore<AppSettings> settings,
        CancellationToken cancellationToken)
    {
        if (!IsThisComputer(http.Connection.RemoteIpAddress))
        {
            return TypedResults.Problem(L.T("SortingEndpoints.DropOnlyFromThisComputer"), statusCode: StatusCodes.Status403Forbidden, title: L.T("Operations.AccessDenied"));
        }

        var (allowed, destination, _) = await guard.CheckAsync(request.Destination, cancellationToken).ConfigureAwait(false);
        if (!allowed)
        {
            return destination is null
                ? ApiProblems.Invalid("destination", L.F("OperationEndpoints.PathMustBeAbsolute", request.Destination))
                : TypedResults.Problem(L.F("OperationEndpoints.PathIsOutsideTheAddedRoots", destination), statusCode: StatusCodes.Status403Forbidden, title: L.T("Operations.AccessDenied"));
        }

        if (!settings.Current.Sorting.Inboxes.Any(inbox => paths.TryNormalize(inbox.Path, out var normalized) && paths.Comparer.Equals(normalized, destination)))
        {
            return TypedResults.Problem(L.F("SortingEndpoints.NotAnInbox", destination), statusCode: StatusCodes.Status403Forbidden, title: L.T("Operations.AccessDenied"));
        }

        if (!Directory.Exists(destination))
        {
            return ApiProblems.NotFound(L.F("OperationEndpoints.FolderNotFound", destination));
        }

        var sources = new List<string>(request.Sources.Count);
        foreach (var path in request.Sources)
        {
            if (!paths.TryNormalize(path, out var normalized))
            {
                return ApiProblems.Invalid("sources", L.F("OperationEndpoints.PathMustBeAbsolute", path));
            }

            if (!File.Exists(normalized) && !Directory.Exists(normalized))
            {
                return ApiProblems.NotFound(L.F("SortingEndpoints.SourceNotFound", normalized));
            }

            sources.Add(normalized);
        }

        var result = await sorting.DropAsync(new DropRequest(sources, destination, request.Copy), cancellationToken).ConfigureAwait(false);
        if (result.TryGetValue(out var info))
        {
            return TypedResults.Accepted(ApiRoutes.V1.Operation(info.Id), info.ToDto());
        }

        return result.Error!.Code == SortingService.InvalidCode
            ? ApiProblems.Invalid("request", result.Error.Message)
            : ApiProblems.Domain(result.Error);
    }

    /// <summary>
    /// Запрос пришёл с этого компьютера. В локальном режиме других и не бывает; с удалённым доступом (этап 13) бросок
    /// файлов из-за пределов корней останется только для клиентов на этой машине.
    /// </summary>
    internal static bool IsThisComputer(IPAddress? address) => address is not null && IPAddress.IsLoopback(address);
}

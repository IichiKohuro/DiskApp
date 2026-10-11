using DiskApp.Contracts;
using DiskApp.Contracts.Operations;
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
/// с тегами. Все пути — только внутри добавленных корней (403 иначе); перенос — операция общей очереди.
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
}

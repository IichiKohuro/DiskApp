using DiskApp.Contracts;
using DiskApp.Contracts.History;
using DiskApp.Core.History;
using DiskApp.Server.Localization;
using DiskApp.Server.Mapping;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

using CoreKind = DiskApp.Core.History.FileEventKind;
using CoreSource = DiskApp.Core.History.FileEventSource;

namespace DiskApp.Server.Endpoints;

/// <summary>
/// История файлов (PLAN.md, этап 20.6): лента событий с отбором, история одного файла через переносы и «Вернуть на место»
/// или «Восстановить» по событию. Пути — только внутри добавленных корней.
/// </summary>
internal static class FileHistoryEndpoints
{
    public static void MapFileHistory(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(ApiRoutes.V1.FileHistory, QueryAsync)
            .WithTags("FileHistory")
            .WithName("GetFileHistory")
            .WithSummary("Лента истории файлов, новые сверху")
            .WithDescription("folder — события внутри папки; q — имя или прежнее имя; kinds и sources — через запятую; from и to — время UTC; before — номер события для следующей страницы; limit — до 500.");

        endpoints.MapGet(ApiRoutes.V1.FileHistoryOfPath, GetOfPathAsync)
            .WithTags("FileHistory")
            .WithName("GetFileHistoryOfPath")
            .WithSummary("История одного файла или папки по пути, через переносы и переименования")
            .ProducesProblem(StatusCodes.Status403Forbidden);

        endpoints.MapPost(ApiRoutes.V1.FileHistoryRevert, RevertAsync)
            .WithTags("FileHistory")
            .WithName("RevertFileEvent")
            .WithSummary("Вернуть на место (переименование, перенос) или восстановить из корзины")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<Results<Ok<FileHistoryPageDto>, ValidationProblem>> QueryAsync(
        FileHistoryService history,
        string? folder,
        string? q,
        string? kinds,
        string? sources,
        DateTimeOffset? from,
        DateTimeOffset? to,
        long? before,
        int? limit,
        CancellationToken cancellationToken)
    {
        if (!TryParse<Contracts.History.FileEventKind, CoreKind>(kinds, out var kindList, out var badKind))
        {
            return ApiProblems.Invalid(nameof(kinds), L.F("HistoryEndpoints.UnknownKind", badKind));
        }

        if (!TryParse<Contracts.History.FileEventSource, CoreSource>(sources, out var sourceList, out var badSource))
        {
            return ApiProblems.Invalid(nameof(sources), L.F("HistoryEndpoints.UnknownSource", badSource));
        }

        var size = Math.Clamp(limit ?? 100, 1, FileHistoryQuery.MaxLimit);
        var items = await history.QueryAsync(
            new FileHistoryQuery
            {
                Folder = string.IsNullOrWhiteSpace(folder) ? null : folder,
                Text = string.IsNullOrWhiteSpace(q) ? null : q,
                Kinds = kindList,
                Sources = sourceList,
                FromUtc = from,
                ToUtc = to,
                BeforeId = before,
                Limit = size,
            },
            cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new FileHistoryPageDto([.. items.Select(item => item.ToDto())], items.Count == size ? items[^1].Id : null));
    }

    private static async Task<Results<Ok<IReadOnlyList<FileEventDto>>, ValidationProblem, ProblemHttpResult>> GetOfPathAsync(
        string? path,
        FileHistoryService history,
        RootGuard guard,
        CancellationToken cancellationToken)
    {
        var (allowed, normalized, _) = await guard.CheckAsync(path ?? string.Empty, cancellationToken).ConfigureAwait(false);
        if (!allowed)
        {
            return normalized is null
                ? ApiProblems.Invalid(nameof(path), L.F("OperationEndpoints.PathMustBeAbsolute", path ?? string.Empty))
                : TypedResults.Problem(L.F("OperationEndpoints.PathIsOutsideTheAddedRoots", normalized), statusCode: StatusCodes.Status403Forbidden, title: L.T("Operations.AccessDenied"));
        }

        var items = await history.GetFileHistoryAsync(normalized!, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<FileEventDto>>([.. items.Select(item => item.ToDto())]);
    }

    private static async Task<Results<Ok<FileHistoryRevertDto>, ProblemHttpResult>> RevertAsync(
        long id,
        FileHistoryService history,
        RootGuard guard,
        CancellationToken cancellationToken)
    {
        if (await history.GetAsync(id, cancellationToken).ConfigureAwait(false) is not { } item)
        {
            return ApiProblems.NotFound(L.T("HistoryEndpoints.EventNotFound"));
        }

        // Возвращать можно только внутри корней: и туда, где файл сейчас, и туда, где он был.
        foreach (var path in new[] { item.Path, item.OldPath }.OfType<string>())
        {
            var (allowed, normalized, _) = await guard.CheckAsync(path, cancellationToken).ConfigureAwait(false);
            if (!allowed)
            {
                return TypedResults.Problem(L.F("OperationEndpoints.PathIsOutsideTheAddedRoots", normalized ?? path), statusCode: StatusCodes.Status403Forbidden, title: L.T("Operations.AccessDenied"));
            }
        }

        var result = await history.RevertAsync(id, cancellationToken).ConfigureAwait(false);
        if (result.TryGetValue(out var reverted))
        {
            return TypedResults.Ok(new FileHistoryRevertDto(reverted.Operation?.ToDto(), reverted.Restored?.ToDto()));
        }

        return result.Error!.Code == FileHistoryService.NotFoundCode
            ? ApiProblems.NotFound(result.Error.Message)
            : TypedResults.Problem(result.Error.Message, statusCode: StatusCodes.Status409Conflict, title: L.T("HistoryEndpoints.CannotRevert"));
    }

    private static bool TryParse<TContract, TCore>(string? text, out IReadOnlyList<TCore> values, out string bad)
        where TContract : struct, Enum
        where TCore : struct, Enum
    {
        var result = new List<TCore>();
        bad = string.Empty;
        foreach (var part in (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<TContract>(part, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                bad = part;
                values = [];
                return false;
            }

            result.Add(Enum.Parse<TCore>(parsed.ToString()));
        }

        values = result;
        return true;
    }
}

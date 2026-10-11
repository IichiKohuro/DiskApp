using DiskApp.Contracts;
using DiskApp.Contracts.App;
using DiskApp.Core.Abstractions;
using DiskApp.Core.Indexing;
using DiskApp.Infrastructure.Sqlite;
using DiskApp.Server.Hosting;
using DiskApp.Server.Localization;
using DiskApp.Server.Mapping;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace DiskApp.Server.Endpoints;

/// <summary>
/// <c>GET /health</c>: сервер жив, база открыта, состояние индексации.
/// </summary>
internal static class HealthEndpoints
{
    public static void MapHealth(this IEndpointRouteBuilder endpoints)
        => endpoints.MapGet(ApiRoutes.Health, Get)
            .WithName("GetHealth")
            .WithTags("App")
            .WithSummary("Проверка живости сервера")
            .WithDescription("Версия приложения, время работы, версия схемы базы и общее состояние индексации. 503 — база ещё не открыта.");

    private static Results<Ok<HealthDto>, ProblemHttpResult> Get(ServerState state, SqliteDatabase database, IndexerService indexer, IClock clock)
    {
        if (!state.IsDatabaseReady)
        {
            return TypedResults.Problem(L.T("HealthEndpoints.TheDatabaseIsNotOpenYet"), statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var uptime = Math.Max(0, (long)(clock.UtcNow - state.StartedUtc).TotalSeconds);
        return TypedResults.Ok(new HealthDto("ok", state.Version, uptime, database.GetSchemaVersion(), indexer.GetAggregateState().ToDto()));
    }
}

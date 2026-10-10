using DiskApp.Contracts;
using DiskApp.Server.Endpoints;
using DiskApp.Server.Hosting;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace DiskApp.Server;

/// <summary>
/// Конвейер и маршруты сервера (PLAN.md, этапы 4.1 и 4.3).
/// </summary>
public static class ServerEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Промежуточные слои: краткий журнал запросов, обработчик исключений и коды ошибок в формате ProblemDetails.
    /// </summary>
    public static IApplicationBuilder UseDiskAppServer(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseMiddleware<RequestLoggingMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        return app;
    }

    /// <summary>
    /// Маршруты: <c>/health</c> и группа <c>/api/v1</c>.
    /// </summary>
    /// <returns>Группа <c>/api/v1</c> для дополнительных маршрутов.</returns>
    public static RouteGroupBuilder MapDiskAppApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapHealth();
        return endpoints.MapGroup(ApiRoutes.V1.Prefix);
    }
}

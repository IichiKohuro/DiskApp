using DiskApp.Contracts;
using DiskApp.Server.Endpoints;
using DiskApp.Server.Hosting;
using DiskApp.Server.Realtime;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace DiskApp.Server;

/// <summary>
/// Конвейер и маршруты сервера (PLAN.md, этапы 4.1 и 4.3).
/// </summary>
public static class ServerEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Промежуточные слои: краткий журнал запросов, обработчик исключений и коды ошибок в формате ProblemDetails,
    /// проверки локального режима (loopback, чужой Origin, токен сессии).
    /// </summary>
    public static IApplicationBuilder UseDiskAppServer(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseMiddleware<RequestLoggingMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseMiddleware<LocalSecurityMiddleware>();
        return app;
    }

    /// <summary>
    /// Маршруты: <c>/health</c>, API <c>/api/v1</c> — корни и индекс, поиск, записи и действия, избранное, теги,
    /// коллекции, настройки — и хаб событий <c>/hubs/diskapp</c>.
    /// </summary>
    /// <returns>Группа <c>/api/v1</c> для дополнительных маршрутов.</returns>
    public static RouteGroupBuilder MapDiskAppApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapHealth();
        endpoints.MapRoots();
        endpoints.MapSearch();
        endpoints.MapEntries();
        endpoints.MapLibrary();
        endpoints.MapSettings();
        endpoints.MapOperations();
        endpoints.MapSorting();
        endpoints.MapHub<DiskAppHub>(HubRoutes.Path);
        return endpoints.MapGroup(ApiRoutes.V1.Prefix);
    }
}

using DiskApp.Contracts;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace DiskApp.Server.Hosting;

/// <summary>
/// Сервер останавливается — новых подключений к хабу нет (FEEDBACK.md, 2026-10-14). При остановке SignalR закрывает
/// соединения, клиент приложения тут же переподключается (первая пауза — ноль), а Kestrel, который останавливается
/// первым, ждёт это новое соединение до таймаута оболочки — выход затягивался ровно на 5 с. Ответ 503 отправляет клиента
/// на следующую попытку, к которой сервер уже не слушает. Обычные запросы, начатые до остановки, доходят как раньше.
/// </summary>
internal sealed class HubShutdownMiddleware(RequestDelegate next, IHostApplicationLifetime lifetime)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (lifetime.ApplicationStopping.IsCancellationRequested && context.Request.Path.StartsWithSegments(HubRoutes.Path))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.Connection = "close";
            return Task.CompletedTask;
        }

        return next(context);
    }
}

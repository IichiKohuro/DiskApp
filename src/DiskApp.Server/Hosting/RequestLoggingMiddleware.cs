using System.Diagnostics;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DiskApp.Server.Hosting;

/// <summary>
/// Краткий журнал запросов: метод, путь, код ответа, время. Успешные — на уровне Debug, ошибки сервера — Warning.
/// Стоит снаружи обработчика исключений, поэтому видит итоговый код ответа.
/// </summary>
internal sealed class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var status = context.Response.StatusCode;
            var path = context.Request.Path.Value ?? string.Empty;
            if (status >= StatusCodes.Status500InternalServerError)
            {
                ServerLog.RequestFailed(logger, context.Request.Method, path, status, elapsed);
            }
            else
            {
                ServerLog.Request(logger, context.Request.Method, path, status, elapsed);
            }
        }
    }
}

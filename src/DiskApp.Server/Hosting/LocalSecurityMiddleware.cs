using System.Net;
using System.Security.Cryptography;
using System.Text;

using DiskApp.Contracts;
using DiskApp.Server.Localization;

using Microsoft.AspNetCore.Http;

namespace DiskApp.Server.Hosting;

/// <summary>
/// Безопасность локального режима (PLAN.md, этап 4.6):
/// <list type="bullet">
/// <item>заголовок <c>Host</c> — только loopback: страница, подменившая DNS своего домена на 127.0.0.1, не достучится;</item>
/// <item>чужой <c>Origin</c> запрещён: браузерная страница не может отправить запрос к API (CORS выключен, а «простые»
/// запросы без предварительной проверки отсекаются здесь);</item>
/// <item>токен сессии, если он задан, — в заголовке <c>X-DiskApp-Token</c> (для хаба — ещё и в параметре
/// <c>access_token</c>); <c>/health</c> открыт.</item>
/// </list>
/// </summary>
internal sealed class LocalSecurityMiddleware(RequestDelegate next, DiskAppServerOptions options)
{
    private readonly byte[]? _token = options.SessionToken is { Length: > 0 } token ? Encoding.UTF8.GetBytes(token) : null;

    public Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (!IsLoopbackHost(request.Host.Host))
        {
            return RejectAsync(context, StatusCodes.Status403Forbidden, L.T("LocalSecurityMiddleware.RequestsAreAcceptedOnlyAtThe"));
        }

        if (request.Headers.Origin is { Count: > 0 } origin && !IsSameOrigin(origin.ToString(), request))
        {
            return RejectAsync(context, StatusCodes.Status403Forbidden, L.T("LocalSecurityMiddleware.RequestsFromThirdPartyPagesAre"));
        }

        if (_token is not null && !request.Path.StartsWithSegments(ApiRoutes.Health) && !HasToken(request))
        {
            return RejectAsync(context, StatusCodes.Status401Unauthorized, L.T("LocalSecurityMiddleware.ASessionTokenIsRequiredIn") + ApiHeaders.SessionToken + ".");
        }

        return next(context);
    }

    private static bool IsLoopbackHost(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));

    private static bool IsSameOrigin(string origin, HttpRequest request)
        => Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && string.Equals(uri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
            && IsLoopbackHost(uri.Host)
            && uri.Port == (request.Host.Port ?? (request.IsHttps ? 443 : 80));

    private bool HasToken(HttpRequest request)
    {
        var supplied = request.Headers[ApiHeaders.SessionToken].ToString();
        if (supplied.Length == 0 && request.Path.StartsWithSegments(HubRoutes.Path))
        {
            supplied = request.Query[ApiHeaders.SessionTokenQuery].ToString();
        }

        return supplied.Length > 0 && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), _token);
    }

    private static Task RejectAsync(HttpContext context, int statusCode, string detail)
    {
        context.Response.StatusCode = statusCode;
        return Results.Problem(detail, statusCode: statusCode, title: statusCode == StatusCodes.Status401Unauthorized ? L.T("LocalSecurityMiddleware.AuthorizationRequired") : L.T("Security.AccessDenied"))
            .ExecuteAsync(context);
    }
}

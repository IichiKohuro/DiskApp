using DiskApp.Contracts;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;

using Polly;

namespace DiskApp.Client;

/// <summary>
/// Параметры клиента.
/// </summary>
public sealed record DiskAppClientOptions
{
    /// <summary>Адрес удалённого сервера; <c>null</c> — сервер внутри процесса (<see cref="InProcessServerEndpointProvider"/>).</summary>
    public ServerEndpoint? Endpoint { get; init; }

    /// <summary>Предел одной попытки запроса (до заголовков ответа).</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Сколько раз повторить идемпотентный GET при сбое сети или ответе 5xx.</summary>
    public int RetryCount { get; init; } = 3;

    /// <summary>Пауза перед первым повтором; дальше — экспоненциально со случайным разбросом.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Параметры хаба.</summary>
    public DiskAppHubOptions Hub { get; init; } = new();
}

/// <summary>
/// Регистрация клиента в DI (PLAN.md, этап 5.3).
/// </summary>
public static class ClientServiceCollectionExtensions
{
    /// <summary>Хост-заглушка: настоящий адрес подставляет обработчик перед отправкой.</summary>
    internal static readonly Uri PlaceholderAddress = new("http://diskapp.server/");

    /// <summary>
    /// Регистрирует <see cref="IDiskAppApi"/> (типизированный <see cref="HttpClient"/> с повторами только для GET и таймаутом
    /// попытки), <see cref="DiskAppHubConnection"/> и источник адреса сервера: заданный или внутри процесса.
    /// </summary>
    public static IServiceCollection AddDiskAppClient(this IServiceCollection services, DiskAppClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        options ??= new DiskAppClientOptions();

        if (options.Endpoint is { } endpoint)
        {
            services.AddSingleton<IServerEndpointProvider>(new FixedServerEndpointProvider(endpoint));
        }
        else
        {
            services.AddSingleton<InProcessServerEndpointProvider>();
            services.AddSingleton<IServerEndpointProvider>(static provider => provider.GetRequiredService<InProcessServerEndpointProvider>());
        }

        services.AddTransient<EndpointHandler>();
        var http = services.AddHttpClient<IDiskAppApi, HttpDiskAppApi>(static client =>
        {
            client.BaseAddress = PlaceholderAddress;
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        // Сначала устойчивость (внешний обработчик), внутри — подстановка адреса: каждая попытка берёт актуальный адрес.
        http.AddResilienceHandler("diskapp", pipeline =>
            {
                var retry = new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = Math.Max(1, options.RetryCount),
                    BackoffType = DelayBackoffType.Exponential,
                    Delay = options.RetryDelay,
                    UseJitter = true,
                };
                retry.DisableForUnsafeHttpMethods();
                pipeline.AddRetry(retry);
                pipeline.AddTimeout(options.AttemptTimeout);
            });
        http.AddHttpMessageHandler<EndpointHandler>();

        services.AddSingleton(options.Hub);
        services.AddSingleton(static provider => new DiskAppHubConnection(
            provider.GetRequiredService<IServerEndpointProvider>(),
            provider.GetRequiredService<DiskAppHubOptions>(),
            provider.GetService<ILogger<DiskAppHubConnection>>()));
        return services;
    }

    /// <summary>
    /// Подставляет адрес сервера и токен сессии в каждый запрос.
    /// </summary>
    internal sealed class EndpointHandler(IServerEndpointProvider endpoints) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var endpoint = await endpoints.GetEndpointAsync(cancellationToken).ConfigureAwait(false);
            if (request.RequestUri is { IsAbsoluteUri: true } uri && uri.Host == PlaceholderAddress.Host)
            {
                request.RequestUri = new Uri(endpoint.BaseAddress, uri.PathAndQuery);
            }

            if (endpoint.SessionToken is { } token && !request.Headers.Contains(ApiHeaders.SessionToken))
            {
                request.Headers.Add(ApiHeaders.SessionToken, token);
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}

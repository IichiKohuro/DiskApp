namespace DiskApp.Client;

/// <summary>
/// Куда подключаться: адрес сервера, токен сессии и (для удалённого режима, этап 13) отпечаток сертификата.
/// </summary>
/// <param name="BaseAddress">Адрес сервера, например <c>http://127.0.0.1:53187/</c>.</param>
/// <param name="SessionToken">Токен сессии для заголовка <c>X-DiskApp-Token</c>; <c>null</c> — сервер его не требует.</param>
/// <param name="CertificateThumbprint">Ожидаемый отпечаток сертификата HTTPS (этап 13).</param>
public sealed record ServerEndpoint(Uri BaseAddress, string? SessionToken = null, string? CertificateThumbprint = null);

/// <summary>
/// Источник адреса сервера (PLAN.md, этап 5.3): внутри процесса адрес известен только после старта Kestrel,
/// для удалённого подключения он задан заранее.
/// </summary>
public interface IServerEndpointProvider
{
    /// <summary>Адрес сервера; ждёт, пока он станет известен.</summary>
    ValueTask<ServerEndpoint> GetEndpointAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Заранее известный адрес: удалённый сервер или тесты.
/// </summary>
public sealed class FixedServerEndpointProvider(ServerEndpoint endpoint) : IServerEndpointProvider
{
    private readonly ServerEndpoint _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));

    /// <inheritdoc />
    public ValueTask<ServerEndpoint> GetEndpointAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(_endpoint);
}

/// <summary>
/// Адрес сервера внутри процесса: десктоп задаёт его после старта сервера, запросы до этого ждут.
/// </summary>
public sealed class InProcessServerEndpointProvider : IServerEndpointProvider
{
    private readonly TaskCompletionSource<ServerEndpoint> _endpoint = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Адрес уже известен.</summary>
    public bool IsReady => _endpoint.Task.IsCompletedSuccessfully;

    /// <summary>
    /// Задаёт адрес запущенного сервера; повторный вызов ничего не меняет.
    /// </summary>
    public void SetEndpoint(ServerEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        _endpoint.TrySetResult(endpoint);
    }

    /// <summary>Сервер не запустился: ожидающие запросы получают ошибку.</summary>
    public void Fail(Exception exception) => _endpoint.TrySetException(exception);

    /// <inheritdoc />
    public ValueTask<ServerEndpoint> GetEndpointAsync(CancellationToken cancellationToken = default)
        => _endpoint.Task.IsCompletedSuccessfully ? ValueTask.FromResult(_endpoint.Task.Result) : new ValueTask<ServerEndpoint>(_endpoint.Task.WaitAsync(cancellationToken));
}

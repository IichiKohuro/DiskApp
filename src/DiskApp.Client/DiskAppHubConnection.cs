using DiskApp.Contracts;
using DiskApp.Contracts.App;
using DiskApp.Contracts.Entries;
using DiskApp.Contracts.History;
using DiskApp.Contracts.Hub;
using DiskApp.Contracts.Operations;
using DiskApp.Contracts.Roots;

using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskApp.Client;

/// <summary>
/// Параметры подключения к хабу.
/// </summary>
public sealed record DiskAppHubOptions
{
    /// <summary>Паузы между попытками подключения и переподключения; последняя повторяется бесконечно.</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } =
        [TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    /// <summary>Сколько событий каждого вида хранить, пока на них никто не подписан.</summary>
    public int BufferCapacity { get; init; } = 256;
}

/// <summary>
/// Подключение к хабу событий (PLAN.md, этап 5.2): автоматическое переподключение без предела попыток, события как
/// C#-события, состояние соединения. После переподключения подписки на корни восстанавливаются, а сервер сам присылает
/// текущее состояние (<see cref="StatusReceived"/>). События, на которые ещё никто не подписан, копятся в буфере
/// и достаются первому подписчику.
/// </summary>
public sealed partial class DiskAppHubConnection : IAsyncDisposable
{
    private readonly IServerEndpointProvider _endpoints;
    private readonly DiskAppHubOptions _options;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly HashSet<long> _subscriptions = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly EventChannel<IndexProgressDto> _indexProgress;
    private readonly EventChannel<RootStatusChangedDto> _rootStatus;
    private readonly EventChannel<EntriesChangedDto> _entriesChanged;
    private readonly EventChannel<OperationProgressDto> _operationProgress;
    private readonly EventChannel<OperationCompletedDto> _operationCompleted;
    private readonly EventChannel<ConflictDto> _operationNeedsDecision;
    private readonly EventChannel<SettingsDto> _settingsChanged;
    private readonly EventChannel<FileHistoryChangedDto> _fileHistoryChanged;
    private readonly EventChannel<HubStatusDto> _status;
    private HubConnection? _connection;
    private Task? _connecting;
    private int _disposed;

    /// <summary>
    /// Создаёт подключение; соединение устанавливает <see cref="StartAsync"/>.
    /// </summary>
    public DiskAppHubConnection(IServerEndpointProvider endpoints, DiskAppHubOptions? options = null, ILogger<DiskAppHubConnection>? logger = null)
    {
        _endpoints = endpoints ?? throw new ArgumentNullException(nameof(endpoints));
        _options = options ?? new DiskAppHubOptions();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        var capacity = Math.Max(1, _options.BufferCapacity);
        _indexProgress = new(capacity, OnHandlerFailed);
        _rootStatus = new(capacity, OnHandlerFailed);
        _entriesChanged = new(capacity, OnHandlerFailed);
        _operationProgress = new(capacity, OnHandlerFailed);
        _operationCompleted = new(capacity, OnHandlerFailed);
        _operationNeedsDecision = new(capacity, OnHandlerFailed);
        _settingsChanged = new(capacity, OnHandlerFailed);
        _fileHistoryChanged = new(capacity, OnHandlerFailed);
        _status = new(capacity, OnHandlerFailed);
    }

    /// <summary>Состояние соединения изменилось.</summary>
    public event EventHandler<HubConnectionState>? StateChanged;

    /// <summary>Ход индексации корня.</summary>
    public event Action<IndexProgressDto> IndexProgress { add => _indexProgress.Add(value); remove => _indexProgress.Remove(value); }

    /// <summary>Смена состояния корня.</summary>
    public event Action<RootStatusChangedDto> RootStatusChanged { add => _rootStatus.Add(value); remove => _rootStatus.Remove(value); }

    /// <summary>Изменения записей подписанных корней.</summary>
    public event Action<EntriesChangedDto> EntriesChanged { add => _entriesChanged.Add(value); remove => _entriesChanged.Remove(value); }

    /// <summary>Ход файловой операции.</summary>
    public event Action<OperationProgressDto> OperationProgress { add => _operationProgress.Add(value); remove => _operationProgress.Remove(value); }

    /// <summary>Файловая операция завершена.</summary>
    public event Action<OperationCompletedDto> OperationCompleted { add => _operationCompleted.Add(value); remove => _operationCompleted.Remove(value); }

    /// <summary>Файловая операция ждёт решения по конфликту.</summary>
    public event Action<ConflictDto> OperationNeedsDecision { add => _operationNeedsDecision.Add(value); remove => _operationNeedsDecision.Remove(value); }

    /// <summary>Настройки изменены.</summary>
    public event Action<SettingsDto> SettingsChanged { add => _settingsChanged.Add(value); remove => _settingsChanged.Remove(value); }

    /// <summary>В историю файлов записаны новые события (этап 20).</summary>
    public event Action<FileHistoryChangedDto> FileHistoryChanged { add => _fileHistoryChanged.Add(value); remove => _fileHistoryChanged.Remove(value); }

    /// <summary>Текущее состояние индексации и операций: после каждого подключения и переподключения.</summary>
    public event Action<HubStatusDto> StatusReceived { add => _status.Add(value); remove => _status.Remove(value); }

    /// <summary>Состояние соединения.</summary>
    public HubConnectionState State => _connection?.State ?? HubConnectionState.Disconnected;

    /// <summary>Корни, на которые подписан клиент.</summary>
    public IReadOnlyCollection<long> Subscriptions
    {
        get
        {
            lock (_gate)
            {
                return [.. _subscriptions];
            }
        }
    }

    /// <summary>
    /// Подключается к хабу, повторяя попытки, пока сервер недоступен; завершается после первого успешного подключения.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        lock (_gate)
        {
            _connecting ??= ConnectWithRetryAsync();
            return _connecting.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Подписаться на изменения записей корня; подписка переживает переподключения.</summary>
    public async Task SubscribeRootAsync(long rootId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _subscriptions.Add(rootId);
        }

        if (_connection is { State: HubConnectionState.Connected } connection)
        {
            await connection.InvokeAsync(HubRoutes.SubscribeRoot, rootId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Отписаться от изменений записей корня.</summary>
    public async Task UnsubscribeRootAsync(long rootId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _subscriptions.Remove(rootId);
        }

        if (_connection is { State: HubConnectionState.Connected } connection)
        {
            await connection.InvokeAsync(HubRoutes.UnsubscribeRoot, rootId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Текущее состояние индексации и активные операции.</summary>
    public Task<HubStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
        => (_connection ?? throw new InvalidOperationException("Подключение не запущено.")).InvokeAsync<HubStatusDto>(HubRoutes.GetStatus, cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        if (_connection is { } connection)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _stop.Dispose();
    }

    private async Task ConnectWithRetryAsync()
    {
        var endpoint = await _endpoints.GetEndpointAsync(_stop.Token).ConfigureAwait(false);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(endpoint.BaseAddress, HubRoutes.Path), http =>
            {
                if (endpoint.SessionToken is { } token)
                {
                    http.Headers[ApiHeaders.SessionToken] = token;
                }
            })
            .AddJsonProtocol(static json => json.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, DiskAppJsonContext.Default))
            .WithAutomaticReconnect(new EndlessRetryPolicy(_options.RetryDelays))
            .Build();

        connection.On<IndexProgressDto>(nameof(IDiskAppHubClient.OnIndexProgress), _indexProgress.Raise);
        connection.On<RootStatusChangedDto>(nameof(IDiskAppHubClient.OnRootStatusChanged), _rootStatus.Raise);
        connection.On<EntriesChangedDto>(nameof(IDiskAppHubClient.OnEntriesChanged), _entriesChanged.Raise);
        connection.On<OperationProgressDto>(nameof(IDiskAppHubClient.OnOperationProgress), _operationProgress.Raise);
        connection.On<OperationCompletedDto>(nameof(IDiskAppHubClient.OnOperationCompleted), _operationCompleted.Raise);
        connection.On<ConflictDto>(nameof(IDiskAppHubClient.OnOperationNeedsDecision), _operationNeedsDecision.Raise);
        connection.On<SettingsDto>(nameof(IDiskAppHubClient.OnSettingsChanged), _settingsChanged.Raise);
        connection.On<FileHistoryChangedDto>(nameof(IDiskAppHubClient.OnFileHistoryChanged), _fileHistoryChanged.Raise);
        connection.On<HubStatusDto>(nameof(IDiskAppHubClient.OnStatus), _status.Raise);
        connection.Reconnecting += _ => OnStateChangedAsync(HubConnectionState.Reconnecting);
        connection.Reconnected += OnReconnectedAsync;
        connection.Closed += OnClosedAsync;
        _connection = connection;

        await StartConnectionAsync(connection).ConfigureAwait(false);
    }

    private async Task StartConnectionAsync(HubConnection connection)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await connection.StartAsync(_stop.Token).ConfigureAwait(false);
                await ResubscribeAsync(connection).ConfigureAwait(false);
                await OnStateChangedAsync(HubConnectionState.Connected).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or TimeoutException or InvalidOperationException && !_stop.IsCancellationRequested)
            {
                LogConnectFailed(_logger, exception, attempt + 1);
                await Task.Delay(Delay(attempt), _stop.Token).ConfigureAwait(false);
            }
        }
    }

    private TimeSpan Delay(int attempt)
        => _options.RetryDelays.Count == 0 ? TimeSpan.FromSeconds(5) : _options.RetryDelays[Math.Min(attempt, _options.RetryDelays.Count - 1)];

    private async Task OnReconnectedAsync(string? connectionId)
    {
        if (_connection is { } connection)
        {
            await ResubscribeAsync(connection).ConfigureAwait(false);
        }

        await OnStateChangedAsync(HubConnectionState.Connected).ConfigureAwait(false);
    }

    private async Task OnClosedAsync(Exception? exception)
    {
        await OnStateChangedAsync(HubConnectionState.Disconnected).ConfigureAwait(false);
        if (!_stop.IsCancellationRequested && _connection is { } connection)
        {
            // Автоматическое переподключение сдалось или соединение закрыто с ошибкой: начинаем заново.
            _ = Task.Run(() => StartConnectionAsync(connection), CancellationToken.None);
        }
    }

    private async Task ResubscribeAsync(HubConnection connection)
    {
        foreach (var rootId in Subscriptions)
        {
            await connection.InvokeAsync(HubRoutes.SubscribeRoot, rootId, _stop.Token).ConfigureAwait(false);
        }
    }

    private Task OnStateChangedAsync(HubConnectionState state)
    {
        try
        {
            StateChanged?.Invoke(this, state);
        }
        catch (Exception exception)
        {
            OnHandlerFailed(exception);
        }

        return Task.CompletedTask;
    }

    private void OnHandlerFailed(Exception exception) => LogHandlerFailed(_logger, exception);

    [LoggerMessage(EventId = 5001, Level = LogLevel.Debug, Message = "Хаб недоступен, попытка {Attempt}")]
    private static partial void LogConnectFailed(ILogger logger, Exception exception, int attempt);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Warning, Message = "Обработчик события хаба завершился ошибкой")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception);

    /// <summary>
    /// Переподключение без предела попыток: сервер внутри процесса может перезапуститься, удалённый — вернуться в сеть.
    /// </summary>
    private sealed class EndlessRetryPolicy(IReadOnlyList<TimeSpan> delays) : IRetryPolicy
    {
        public TimeSpan? NextRetryDelay(RetryContext retryContext)
            => delays.Count == 0 ? TimeSpan.FromSeconds(5) : delays[(int)Math.Min(retryContext.PreviousRetryCount, delays.Count - 1)];
    }

    /// <summary>
    /// Событие с буфером: пока подписчиков нет, значения копятся (не больше ёмкости), первый подписчик получает их сразу.
    /// Ошибка подписчика не ломает приём сообщений.
    /// </summary>
    private sealed class EventChannel<T>(int capacity, Action<Exception> onError)
    {
        private readonly Lock _gate = new();
        private readonly Queue<T> _buffer = new();
        private Action<T>? _handlers;

        public void Add(Action<T>? handler)
        {
            if (handler is null)
            {
                return;
            }

            T[] pending;
            lock (_gate)
            {
                _handlers += handler;
                pending = [.. _buffer];
                _buffer.Clear();
            }

            foreach (var item in pending)
            {
                Invoke(handler, item);
            }
        }

        public void Remove(Action<T>? handler)
        {
            lock (_gate)
            {
                _handlers -= handler;
            }
        }

        public void Raise(T item)
        {
            Action<T>? handlers;
            lock (_gate)
            {
                handlers = _handlers;
                if (handlers is null)
                {
                    if (_buffer.Count >= capacity)
                    {
                        _buffer.Dequeue();
                    }

                    _buffer.Enqueue(item);
                    return;
                }
            }

            foreach (var handler in handlers.GetInvocationList().Cast<Action<T>>())
            {
                Invoke(handler, item);
            }
        }

        private void Invoke(Action<T> handler, T item)
        {
            try
            {
                handler(item);
            }
            catch (Exception exception)
            {
                onError(exception);
            }
        }
    }
}

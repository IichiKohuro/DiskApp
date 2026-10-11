using DiskApp.Contracts.App;
using DiskApp.Contracts.Entries;
using DiskApp.Contracts.Hub;
using DiskApp.Contracts.Operations;
using DiskApp.Contracts.Roots;
using DiskApp.Core.Abstractions;
using DiskApp.Core.Events;
using DiskApp.Core.Settings;
using DiskApp.Server.Mapping;

using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DiskApp.Server.Realtime;

/// <summary>
/// Параметры трансляции событий в хаб.
/// </summary>
public sealed record HubBroadcastOptions
{
    /// <summary>Как часто уходят накопленные события; ход индексации — не чаще одного раза за интервал на корень.</summary>
    public TimeSpan BatchInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Изменений записей в одном сообщении.</summary>
    public int MaxChangesPerMessage { get; init; } = 500;

    /// <summary>Сколько изменений корня копить за интервал; больше — одно сообщение «перечитайте корень».</summary>
    public int MaxPendingChangesPerRoot { get; init; } = 10_000;
}

/// <summary>
/// Транслятор <see cref="IIndexEventBus"/> и настроек в хаб (PLAN.md, этап 4.4). Обработчики шины только складывают
/// события в буферы и не ждут сеть; раз в <see cref="HubBroadcastOptions.BatchInterval"/> буферы уходят клиентам:
/// смены состояния корней, последний ход индексации каждого корня, ход, конфликты и завершение файловых операций,
/// изменения записей пакетами по корням, настройки.
/// </summary>
internal sealed partial class HubBroadcaster(
    IIndexEventBus events,
    ISettingsStore<AppSettings> settings,
    IHubContext<DiskAppHub, IDiskAppHubClient> hub,
    HubBroadcastOptions options,
    ILogger<HubBroadcaster> logger) : BackgroundService
{
    private readonly Lock _gate = new();
    private List<RootStatusChangedDto> _statuses = [];
    private Dictionary<long, IndexProgressDto> _progress = [];
    private Dictionary<long, List<EntryChangeDto>> _changes = [];
    private HashSet<long> _overflowed = [];
    private Dictionary<Guid, OperationProgressDto> _operationProgress = [];
    private List<ConflictDto> _decisions = [];
    private List<OperationCompletedDto> _completed = [];
    private SettingsDto? _settings;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var statusSubscription = events.Subscribe<RootStatusChangedEvent>(OnStatus);
        using var progressSubscription = events.Subscribe<IndexProgressEvent>(OnProgress);
        using var changesSubscription = events.Subscribe<EntriesChangedEvent>(OnChanges);
        using var operationProgressSubscription = events.Subscribe<OperationProgressEvent>(OnOperationProgress);
        using var decisionSubscription = events.Subscribe<OperationNeedsDecisionEvent>(OnDecision);
        using var completedSubscription = events.Subscribe<OperationCompletedEvent>(OnCompleted);
        using var settingsSubscription = settings.Subscribe(OnSettings);
        using var timer = new PeriodicTimer(options.BatchInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await FlushAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Остановка сервера.
        }
    }

    /// <summary>
    /// Отправляет накопленное; ошибки сети журналируются и не останавливают трансляцию.
    /// </summary>
    internal async Task FlushAsync()
    {
        List<RootStatusChangedDto> statuses;
        Dictionary<long, IndexProgressDto> progress;
        Dictionary<long, List<EntryChangeDto>> changes;
        HashSet<long> overflowed;
        Dictionary<Guid, OperationProgressDto> operationProgress;
        List<ConflictDto> decisions;
        List<OperationCompletedDto> completed;
        SettingsDto? changedSettings;
        lock (_gate)
        {
            if (_statuses.Count == 0 && _progress.Count == 0 && _changes.Count == 0 && _overflowed.Count == 0 && _settings is null
                && _operationProgress.Count == 0 && _decisions.Count == 0 && _completed.Count == 0)
            {
                return;
            }

            (statuses, _statuses) = (_statuses, []);
            (progress, _progress) = (_progress, []);
            (changes, _changes) = (_changes, []);
            (overflowed, _overflowed) = (_overflowed, []);
            (operationProgress, _operationProgress) = (_operationProgress, []);
            (decisions, _decisions) = (_decisions, []);
            (completed, _completed) = (_completed, []);
            (changedSettings, _settings) = (_settings, null);
        }

        try
        {
            foreach (var status in statuses)
            {
                await hub.Clients.All.OnRootStatusChanged(status).ConfigureAwait(false);
            }

            foreach (var item in progress.Values)
            {
                await hub.Clients.All.OnIndexProgress(item).ConfigureAwait(false);
            }

            foreach (var item in operationProgress.Values)
            {
                await hub.Clients.All.OnOperationProgress(item).ConfigureAwait(false);
            }

            foreach (var conflict in decisions)
            {
                await hub.Clients.All.OnOperationNeedsDecision(conflict).ConfigureAwait(false);
            }

            foreach (var item in completed)
            {
                await hub.Clients.All.OnOperationCompleted(item).ConfigureAwait(false);
            }

            foreach (var rootId in overflowed)
            {
                await hub.Clients.Group(DiskAppHub.RootGroup(rootId))
                    .OnEntriesChanged(new EntriesChangedDto(rootId, [new EntryChangeDto(Contracts.Entries.EntryChangeKind.Modified, string.Empty, null, true)]))
                    .ConfigureAwait(false);
            }

            foreach (var (rootId, list) in changes)
            {
                foreach (var chunk in list.Chunk(Math.Max(1, options.MaxChangesPerMessage)))
                {
                    await hub.Clients.Group(DiskAppHub.RootGroup(rootId)).OnEntriesChanged(new EntriesChangedDto(rootId, chunk)).ConfigureAwait(false);
                }
            }

            if (changedSettings is not null)
            {
                await hub.Clients.All.OnSettingsChanged(changedSettings).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogBroadcastFailed(logger, exception);
        }
    }

    private void OnStatus(RootStatusChangedEvent change)
    {
        lock (_gate)
        {
            _statuses.Add(change.ToDto());
        }
    }

    private void OnProgress(IndexProgressEvent progress)
    {
        lock (_gate)
        {
            _progress[progress.RootId] = progress.ToDto();
        }
    }

    private void OnChanges(EntriesChangedEvent changed)
    {
        var dto = changed.ToDto();
        lock (_gate)
        {
            if (_overflowed.Contains(changed.RootId))
            {
                return;
            }

            if (!_changes.TryGetValue(changed.RootId, out var list))
            {
                list = [];
                _changes[changed.RootId] = list;
            }

            list.AddRange(dto.Changes);
            if (list.Count > options.MaxPendingChangesPerRoot)
            {
                _changes.Remove(changed.RootId);
                _overflowed.Add(changed.RootId);
            }
        }
    }

    private void OnOperationProgress(OperationProgressEvent progress)
    {
        lock (_gate)
        {
            _operationProgress[progress.OperationId] = progress.ToDto();
        }
    }

    private void OnDecision(OperationNeedsDecisionEvent conflict)
    {
        lock (_gate)
        {
            _decisions.Add(conflict.ToDto());
        }
    }

    private void OnCompleted(OperationCompletedEvent completed)
    {
        lock (_gate)
        {
            _completed.Add(completed.ToDto());
        }
    }

    private void OnSettings(AppSettings changed)
    {
        var dto = changed.ToDto();
        lock (_gate)
        {
            _settings = dto;
        }
    }

    [LoggerMessage(EventId = 4010, Level = LogLevel.Warning, Message = "Не удалось отправить события хаба")]
    private static partial void LogBroadcastFailed(ILogger logger, Exception exception);
}

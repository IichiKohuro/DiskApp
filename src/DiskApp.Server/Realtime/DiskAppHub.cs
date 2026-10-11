using System.Globalization;

using DiskApp.Contracts.Hub;
using DiskApp.Core.Indexing;
using DiskApp.Core.Operations;
using DiskApp.Server.Mapping;

using Microsoft.AspNetCore.SignalR;

namespace DiskApp.Server.Realtime;

/// <summary>
/// Хаб событий (PLAN.md, этап 4.4). После подключения клиент сразу получает <see cref="IDiskAppHubClient.OnStatus"/>,
/// поэтому переподключение не теряет состояния. Изменения записей приходят только подписчикам корня.
/// </summary>
internal sealed class DiskAppHub(IndexerService indexer, FileOperationService operations) : Hub<IDiskAppHubClient>
{
    /// <summary>Группа подписчиков корня.</summary>
    public static string RootGroup(long rootId) => "root:" + rootId.ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.OnStatus(GetStatus()).ConfigureAwait(false);
        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    /// <summary>Подписаться на изменения записей корня.</summary>
    public Task SubscribeRoot(long rootId) => Groups.AddToGroupAsync(Context.ConnectionId, RootGroup(rootId));

    /// <summary>Отписаться от изменений записей корня.</summary>
    public Task UnsubscribeRoot(long rootId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, RootGroup(rootId));

    /// <summary>Текущее состояние индексации и активные операции.</summary>
    public HubStatusDto GetStatus() => new(indexer.ToIndexStatusDto(), operations.GetActive().Select(static info => info.ToDto()).ToArray());
}

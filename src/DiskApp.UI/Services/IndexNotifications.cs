using DiskApp.Client;
using DiskApp.Contracts.Hub;
using DiskApp.Contracts.Roots;
using DiskApp.UI.Localization;
using DiskApp.UI.ViewModels;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.Services;

/// <summary>
/// Уведомления индекса (PLAN.md, этап 21.1): папка индекса стала недоступна, индексация закончилась ошибкой, папка снова
/// доступна. Сообщаются только перемены за сеанс: состояние при подключении к хабу — точка отсчёта, поэтому диск,
/// отключённый ещё до запуска, не напоминает о себе при каждом старте.
/// </summary>
public sealed class IndexNotifications
{
    private readonly INotificationService _notifications;
    private readonly IAppCommands? _commands;
    private readonly Dictionary<long, RootStatus> _statuses = [];
    private readonly Dictionary<long, string> _labels = [];
    private readonly HashSet<long> _offline = [];

    /// <summary>Создаёт источник; без команд приложения у уведомлений нет кнопки «Открыть».</summary>
    public IndexNotifications(INotificationService notifications, IAppCommands? commands = null)
    {
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _commands = commands;
    }

    /// <summary>Слушает хаб; события разбираются на потоке интерфейса.</summary>
    public void Attach(DiskAppHubConnection hub, IDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(dispatcher);
        hub.StatusReceived += status => dispatcher.Post(() => Apply(status));
        hub.RootStatusChanged += change => dispatcher.Post(() => Apply(change));
    }

    /// <summary>Полное состояние при подключении или переподключении: точка отсчёта без уведомлений.</summary>
    public void Apply(HubStatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        _statuses.Clear();
        _offline.Clear();
        foreach (var root in status.Index.Roots)
        {
            _statuses[root.RootId] = root.Status;
            _labels[root.RootId] = root.Label;
            if (root.Status == RootStatus.Offline)
            {
                _offline.Add(root.RootId);
            }
        }
    }

    /// <summary>Состояние корня изменилось.</summary>
    public void Apply(RootStatusChangedDto change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var previous = _statuses.TryGetValue(change.RootId, out var known) ? known : (RootStatus?)null;
        _statuses[change.RootId] = change.Status;
        var label = _labels.TryGetValue(change.RootId, out var name) ? name : L.T("Notifications.SomeFolder");
        var detail = string.IsNullOrWhiteSpace(change.Message) ? null : change.Message.Trim();
        switch (change.Status)
        {
            case RootStatus.Offline when previous != RootStatus.Offline:
                _offline.Add(change.RootId);
                _notifications.Show(new Notification(
                    NotificationKind.Index,
                    L.T("Notifications.RootOffline"),
                    detail is null ? L.F("Notifications.RootOfflineText", label) : L.F("Notifications.RootWithDetail", label, detail))
                {
                    Severity = FAInfoBarSeverity.Warning,
                    Key = "root-offline:" + change.RootId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Actions = OpenHome(),
                });
                break;
            case RootStatus.Error when previous != RootStatus.Error:
                _notifications.Show(new Notification(
                    NotificationKind.Index,
                    L.T("Notifications.IndexError"),
                    detail is null ? L.F("Notifications.IndexErrorText", label) : L.F("Notifications.RootWithDetail", label, detail))
                {
                    Severity = FAInfoBarSeverity.Error,
                    Importance = NotificationImportance.Urgent,
                    Key = "root-error:" + change.RootId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Actions = OpenHome(),
                });
                break;
            case RootStatus.Online or RootStatus.Scanning when _offline.Remove(change.RootId):
                _notifications.Show(new Notification(NotificationKind.Index, L.T("Notifications.RootBack"), L.F("Notifications.RootBackText", label))
                {
                    Severity = FAInfoBarSeverity.Success,
                    Importance = NotificationImportance.Quiet,
                });
                break;
        }
    }

    private IReadOnlyList<NotificationAction> OpenHome()
        => _commands is { } commands
            ? [new NotificationAction(L.T("Notifications.Open"), () =>
            {
                commands.ShowMainWindow();
                commands.NavigateTo(PageKeys.Home);
            })]
            : [];
}

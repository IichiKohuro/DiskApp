using DiskApp.UI.ViewModels;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.Services;

/// <summary>О чём уведомление (PLAN.md, этап 21.1).</summary>
public enum NotificationKind
{
    /// <summary>Прочее.</summary>
    General,

    /// <summary>Сводка авторазбора.</summary>
    AutoSort,

    /// <summary>Файловая операция завершилась или не удалась.</summary>
    Operation,

    /// <summary>Корень недоступен, снова доступен или индексация с ошибкой.</summary>
    Index,

    /// <summary>Резервное копирование (этап 16.1).</summary>
    Backup,

    /// <summary>Напоминание о разборе (этап 21.3).</summary>
    Sorting,

    /// <summary>Своё напоминание (этап 21.4).</summary>
    Reminder,
}

/// <summary>Насколько настойчиво показывать.</summary>
public enum NotificationImportance
{
    /// <summary>Только в главном окне, если оно открыто, иначе — подсказка значка в трее.</summary>
    Quiet,

    /// <summary>В главном окне или тихой карточкой у трея, с общими лимитами и тишиной.</summary>
    Normal,

    /// <summary>Как обычное, но без лимита частоты: ошибки, которые нельзя пропустить. Тишину соблюдает.</summary>
    Urgent,
}

/// <summary>Где уведомление показано.</summary>
public enum NotificationRoute
{
    /// <summary>Полоса уведомлений главного окна.</summary>
    InfoBar,

    /// <summary>Тихая карточка у области уведомлений.</summary>
    Card,

    /// <summary>Только подсказка значка в трее: окно скрыто, а карточку не пустили лимиты, тишина или занятость.</summary>
    Tray,

    /// <summary>Вид выключен в настройках, а главное окно скрыто: уведомление только записано.</summary>
    Off,
}

/// <summary>
/// Настройки канала и планировщика (PLAN.md, этап 21.5): какие виды показывать, настойчивость, часы тишины, «Не
/// беспокоить» Windows и свой «не беспокоить» на время.
/// </summary>
public sealed record NotificationPreferences
{
    /// <summary>Выключенные виды: в полосе главного окна — как раньше, карточек и подсказки в трее нет.</summary>
    public IReadOnlySet<NotificationKind> DisabledKinds { get; init; } = new HashSet<NotificationKind>();

    /// <summary>Карточек в час, кроме срочных.</summary>
    public int MaxCardsPerHour { get; init; } = 4;

    /// <summary>Удобные напоминания — не чаще.</summary>
    public TimeSpan ReminderInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Удобных напоминаний в день — не больше.</summary>
    public int RemindersPerDay { get; init; } = 3;

    /// <summary>Часы тишины; <c>null</c> — нет.</summary>
    public (TimeOnly Start, TimeOnly End)? QuietHours { get; init; }

    /// <summary>Учитывать «Не беспокоить» Windows.</summary>
    public bool RespectWindowsDoNotDisturb { get; init; } = true;

    /// <summary>«Не беспокоить» в DiskApp до этого времени.</summary>
    public DateTimeOffset? DoNotDisturbUntil { get; init; }

    /// <summary>Из настроек приложения: настойчивость задаёт лимиты, неверное время тишины — тишины нет.</summary>
    public static NotificationPreferences From(DiskApp.Contracts.App.NotificationSettingsDto settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var disabled = new HashSet<NotificationKind>();
        if (!settings.AutoSort)
        {
            disabled.Add(NotificationKind.AutoSort);
        }

        if (!settings.Operations)
        {
            disabled.Add(NotificationKind.Operation);
        }

        if (!settings.Index)
        {
            disabled.Add(NotificationKind.Index);
        }

        if (!settings.SortingReminders)
        {
            disabled.Add(NotificationKind.Sorting);
        }

        var (cards, interval, perDay) = settings.Persistence switch
        {
            DiskApp.Contracts.App.NotificationPersistence.Rare => (2, TimeSpan.FromHours(2), 1),
            DiskApp.Contracts.App.NotificationPersistence.Often => (8, TimeSpan.FromMinutes(30), 6),
            _ => (4, TimeSpan.FromHours(1), 3),
        };
        (TimeOnly, TimeOnly)? quiet = settings.QuietHours && TryParse(settings.QuietFrom, out var from) && TryParse(settings.QuietTo, out var to) ? (from, to) : null;
        return new NotificationPreferences
        {
            DisabledKinds = disabled,
            MaxCardsPerHour = cards,
            ReminderInterval = interval,
            RemindersPerDay = perDay,
            QuietHours = quiet,
            RespectWindowsDoNotDisturb = settings.RespectWindowsDoNotDisturb,
            DoNotDisturbUntil = settings.DoNotDisturbUntil,
        };
    }

    private static bool TryParse(string? text, out TimeOnly time)
        => TimeOnly.TryParseExact(text, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out time);
}

/// <summary>Кнопка уведомления.</summary>
/// <param name="Text">Подпись.</param>
/// <param name="Run">Что сделать; уведомление после этого закрывается.</param>
public sealed record NotificationAction(string Text, Action Run);

/// <summary>Уведомление.</summary>
/// <param name="Kind">О чём.</param>
/// <param name="Title">Заголовок.</param>
/// <param name="Text">Текст.</param>
public sealed record Notification(NotificationKind Kind, string Title, string Text)
{
    /// <summary>Насколько настойчиво показывать.</summary>
    public NotificationImportance Importance { get; init; } = NotificationImportance.Normal;

    /// <summary>Цвет и значок: сведения, успех, предупреждение, ошибка.</summary>
    public FAInfoBarSeverity Severity { get; init; } = FAInfoBarSeverity.Informational;

    /// <summary>Кнопки (в карточке — до трёх); в полосе главного окна — только первая.</summary>
    public IReadOnlyList<NotificationAction> Actions { get; init; } = [];

    /// <summary>Одно и то же событие («корень 3 недоступен»): карточкой не чаще <see cref="NotificationService.RepeatInterval"/>.</summary>
    public string? Key { get; init; }
}

/// <summary>Показанное уведомление.</summary>
/// <param name="Notification">Уведомление.</param>
/// <param name="Time">Когда.</param>
/// <param name="Route">Где показано.</param>
public sealed record NotificationRecord(Notification Notification, DateTimeOffset Time, NotificationRoute Route);

/// <summary>
/// Оболочка, в которой живут уведомления: видно ли главное окно и как показать тихую карточку. В десктопе — окно у
/// области уведомлений; без оболочки (превью, тесты) карточек нет, а главное окно считается открытым.
/// </summary>
public interface INotificationHost
{
    /// <summary>Главное окно открыто и не свёрнуто: уведомление уйдёт в его полосу.</summary>
    bool IsMainWindowVisible { get; }

    /// <summary>Показать тихую карточку: без звука, не забирая фокус; прежняя карточка заменяется.</summary>
    void ShowCard(NotificationCardViewModel card);
}

/// <summary>Оболочки нет: всё уходит в полосу главного окна.</summary>
public sealed class NullNotificationHost : INotificationHost
{
    /// <inheritdoc />
    public bool IsMainWindowVisible => true;

    /// <inheritdoc />
    public void ShowCard(NotificationCardViewModel card)
    {
    }
}

/// <summary>
/// Канал уведомлений (PLAN.md, этап 21.1, ADR-0013): все части приложения сообщают о важном только через него, поэтому
/// лимиты частоты, «не беспокоить» и часы тишины — в одном месте. Вызывается на потоке интерфейса.
/// </summary>
public interface INotificationService
{
    /// <summary>Записано новое уведомление или прочитаны старые: подсказка значка в трее обновляется.</summary>
    event EventHandler? Changed;

    /// <summary>Последние уведомления, новые сверху.</summary>
    IReadOnlyList<NotificationRecord> Recent { get; }

    /// <summary>Сколько уведомлений пользователь ещё не видел в главном окне.</summary>
    int UnreadCount { get; }

    /// <summary>Последнее непрочитанное — для подсказки значка в трее.</summary>
    NotificationRecord? LatestUnread { get; }

    /// <summary>«Не беспокоить» в DiskApp до этого времени: карточек нет, остаётся подсказка в трее.</summary>
    DateTimeOffset? DoNotDisturbUntil { get; set; }

    /// <summary>Часы тишины, местное время; конец раньше начала — через полночь. <c>null</c> — тишины нет.</summary>
    (TimeOnly Start, TimeOnly End)? QuietHours { get; set; }

    /// <summary>Показывает уведомление по нарастанию: полоса главного окна → тихая карточка → подсказка в трее.</summary>
    NotificationRoute Show(Notification notification);

    /// <summary>Сейчас тишина: «не беспокоить» DiskApp или часы тишины.</summary>
    bool IsQuiet(DateTimeOffset now);

    /// <summary>Вид включён в настройках.</summary>
    bool IsEnabled(NotificationKind kind);

    /// <summary>Учитывать «Не беспокоить» Windows (полный экран и презентация учитываются всегда).</summary>
    bool RespectWindowsDoNotDisturb { get; }

    /// <summary>Применяет настройки «Уведомлений».</summary>
    void Apply(NotificationPreferences preferences);

    /// <summary>Пользователь открыл главное окно: непрочитанных больше нет.</summary>
    void MarkAllRead();
}

/// <summary>
/// Канал уведомлений по нарастанию (ADR-0013, п. 2). Открыто главное окно — уведомление в его полосе. Окно скрыто —
/// тихая карточка у области уведомлений, если её пускают тишина, занятость и лимиты: «не беспокоить», часы тишины,
/// полный экран, презентация, «Не беспокоить» Windows, заблокированный сеанс, одно и то же событие не чаще
/// <see cref="RepeatInterval"/>, не больше <see cref="MaxCardsPerHour"/> карточек в час (срочное — без этого лимита).
/// Иначе — подсказка значка в трее; выключенный в настройках вид — только запись. Всё остаётся в <see cref="Recent"/>.
/// </summary>
public sealed class NotificationService : INotificationService
{
    private readonly INotificationHost _host;
    private readonly IInfoBarService? _infoBar;
    private readonly IUserActivityMonitor _activity;
    private HashSet<NotificationKind> _disabled = [];
    private readonly Func<DateTimeOffset> _clock;
    private readonly List<NotificationRecord> _recent = [];
    private readonly List<DateTimeOffset> _cards = [];
    private readonly Dictionary<string, DateTimeOffset> _lastCardByKey = new(StringComparer.Ordinal);
    private int _unread;

    /// <summary>Создаёт канал.</summary>
    /// <param name="host">Оболочка: главное окно и карточки.</param>
    /// <param name="infoBar">Полоса уведомлений главного окна.</param>
    /// <param name="clock">Часы; по умолчанию — местное время.</param>
    /// <param name="activity">Занятость пользователя: полный экран, презентация, «Не беспокоить» Windows.</param>
    public NotificationService(INotificationHost host, IInfoBarService? infoBar = null, Func<DateTimeOffset>? clock = null, IUserActivityMonitor? activity = null)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _infoBar = infoBar;
        _clock = clock ?? (static () => DateTimeOffset.Now);
        _activity = activity ?? new NullUserActivityMonitor();
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>Одно и то же событие (по <see cref="Notification.Key"/>) карточкой — не чаще.</summary>
    public TimeSpan RepeatInterval { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Карточек в час, кроме срочных.</summary>
    public int MaxCardsPerHour { get; set; } = 4;

    /// <summary>Сколько уведомлений помнить.</summary>
    public int RecentLimit { get; init; } = 50;

    /// <inheritdoc />
    public IReadOnlyList<NotificationRecord> Recent => _recent;

    /// <inheritdoc />
    public int UnreadCount => _unread;

    /// <inheritdoc />
    public NotificationRecord? LatestUnread => _unread > 0 ? _recent.FirstOrDefault(static record => record.Route is NotificationRoute.Card or NotificationRoute.Tray) : null;

    /// <inheritdoc />
    public DateTimeOffset? DoNotDisturbUntil { get; set; }

    /// <inheritdoc />
    public (TimeOnly Start, TimeOnly End)? QuietHours { get; set; }

    /// <inheritdoc />
    public bool RespectWindowsDoNotDisturb { get; set; } = true;

    /// <inheritdoc />
    public bool IsEnabled(NotificationKind kind) => !_disabled.Contains(kind);

    /// <inheritdoc />
    public void Apply(NotificationPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        _disabled = [.. preferences.DisabledKinds];
        MaxCardsPerHour = preferences.MaxCardsPerHour;
        QuietHours = preferences.QuietHours;
        RespectWindowsDoNotDisturb = preferences.RespectWindowsDoNotDisturb;
        DoNotDisturbUntil = preferences.DoNotDisturbUntil;
    }

    /// <inheritdoc />
    public NotificationRoute Show(Notification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var now = _clock();
        var route = Choose(notification, now);
        switch (route)
        {
            case NotificationRoute.InfoBar:
                ShowInfoBar(notification);
                break;
            case NotificationRoute.Card:
                _cards.Add(now);
                if (notification.Key is { } key)
                {
                    _lastCardByKey[key] = now;
                }

                _host.ShowCard(new NotificationCardViewModel(notification));
                break;
        }

        if (route is NotificationRoute.Card or NotificationRoute.Tray)
        {
            _unread++;
        }

        _recent.Insert(0, new NotificationRecord(notification, now, route));
        if (_recent.Count > RecentLimit)
        {
            _recent.RemoveRange(RecentLimit, _recent.Count - RecentLimit);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return route;
    }

    /// <inheritdoc />
    public void MarkAllRead()
    {
        if (_unread == 0)
        {
            return;
        }

        _unread = 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public bool IsQuiet(DateTimeOffset now)
    {
        if (DoNotDisturbUntil is { } until && now < until)
        {
            return true;
        }

        if (QuietHours is not ({ } start, { } end) || start == end)
        {
            return false;
        }

        var time = TimeOnly.FromDateTime(now.LocalDateTime);
        return start < end ? time >= start && time < end : time >= start || time < end;
    }

    private NotificationRoute Choose(Notification notification, DateTimeOffset now)
    {
        if (_host.IsMainWindowVisible)
        {
            return NotificationRoute.InfoBar;
        }

        if (!IsEnabled(notification.Kind))
        {
            return NotificationRoute.Off;
        }

        if (notification.Importance == NotificationImportance.Quiet || IsQuiet(now) || IsBusy(_activity.Current))
        {
            return NotificationRoute.Tray;
        }

        if (notification.Key is { } key && _lastCardByKey.TryGetValue(key, out var last) && now - last < RepeatInterval)
        {
            return NotificationRoute.Tray;
        }

        _cards.RemoveAll(time => now - time >= TimeSpan.FromHours(1));
        return notification.Importance != NotificationImportance.Urgent && _cards.Count >= MaxCardsPerHour
            ? NotificationRoute.Tray
            : NotificationRoute.Card;
    }

    // Карточка поверх полноэкранной игры или презентации — то, чего ADR-0013 обещает не делать.
    private bool IsBusy(UserActivity activity)
        => activity.IsLocked || activity.Busy switch
        {
            UserBusyState.Free => false,
            UserBusyState.DoNotDisturb => RespectWindowsDoNotDisturb,
            _ => true,
        };

    private void ShowInfoBar(Notification notification)
    {
        if (_infoBar is null)
        {
            return;
        }

        if (notification.Actions.Count > 0)
        {
            var action = notification.Actions[0];
            _infoBar.Show(notification.Title, notification.Text, notification.Severity, action.Text, action.Run);
        }
        else
        {
            _infoBar.Show(notification.Title, notification.Text, notification.Severity);
        }
    }
}

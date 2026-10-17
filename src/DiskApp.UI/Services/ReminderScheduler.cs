using DiskApp.UI.Localization;

namespace DiskApp.UI.Services;

/// <summary>Когда показывать напоминание.</summary>
public enum ReminderTiming
{
    /// <summary>В назначенное время — как только пользователь не занят (полный экран, презентация, тишина ждут).</summary>
    Exact,

    /// <summary>В удобный момент после назначенного времени, с общими лимитами в час и в день.</summary>
    Convenient,
}

/// <summary>Заявка на напоминание (PLAN.md, этап 21.2). Переживает перезапуск.</summary>
/// <param name="Id">Ключ заявки: новая заявка с тем же ключом заменяет прежнюю.</param>
/// <param name="Kind">Вид: по нему источник собирает уведомление (<see cref="ReminderScheduler.Register"/>).</param>
/// <param name="Due">Не раньше.</param>
/// <param name="Timing">Точно или в удобный момент.</param>
public sealed record ReminderRequest(string Id, string Kind, DateTimeOffset Due, ReminderTiming Timing = ReminderTiming.Convenient)
{
    /// <summary>Сколько раз отложено подряд: от этого — следующая отсрочка.</summary>
    public int Snoozes { get; init; }

    /// <summary>Заголовок, если вид сам его не собирает (свои напоминания).</summary>
    public string? Title { get; init; }

    /// <summary>Текст.</summary>
    public string? Text { get; init; }

    /// <summary>Данные источника: путь файла и т. п.</summary>
    public string? Payload { get; init; }
}

/// <summary>Где живёт очередь заявок между запусками.</summary>
public interface IReminderStore
{
    /// <summary>Заявки из прошлого запуска.</summary>
    IReadOnlyList<ReminderRequest> Load();

    /// <summary>Сохраняет очередь; ошибка записи не должна мешать работе.</summary>
    void Save(IReadOnlyList<ReminderRequest> requests);
}

/// <summary>Очередь в памяти: превью, тесты.</summary>
public sealed class MemoryReminderStore : IReminderStore
{
    private IReadOnlyList<ReminderRequest> _requests = [];

    /// <summary>Сколько раз сохраняли.</summary>
    public int Saves { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<ReminderRequest> Load() => _requests;

    /// <inheritdoc />
    public void Save(IReadOnlyList<ReminderRequest> requests)
    {
        _requests = [.. requests];
        Saves++;
    }
}

/// <summary>
/// Планировщик напоминаний (PLAN.md, этап 21.2, ADR-0013, п. 3). Раз в минуту (<see cref="Tick"/>, без диска) смотрит на
/// заявки, у которых подошло время. Пока пользователь занят — полный экран, презентация, «Не беспокоить», заблокированный
/// сеанс, тишина DiskApp — ничего не показывается. Точная заявка показывается сразу, как только он свободен. Заявка «в
/// удобный момент» ждёт хорошего момента: возвращения к компьютеру после простоя от 10 минут (или разблокировки сеанса)
/// и пары минут работы после него; запуск приложения тоже считается возвращением, поэтому пропущенное показывается при
/// запуске. Кто ни разу не отходит, получает напоминание через 2 часа после срока, пока работает. Для них же — лимиты: не
/// чаще раза в час и не больше трёх в день. Показанное сразу откладывается — закрыть без ответа значит «отложить»:
/// 45 минут → 1,5 часа → 3 часа → следующее утро; кнопки уведомления заменяют эту отсрочку своей. Работает на потоке
/// интерфейса.
/// </summary>
public sealed class ReminderScheduler : IDisposable
{
    private static readonly TimeSpan[] Delays = [TimeSpan.FromMinutes(45), TimeSpan.FromMinutes(90), TimeSpan.FromHours(3)];

    private readonly INotificationService _notifications;
    private readonly IUserActivityMonitor _activity;
    private readonly IReminderStore _store;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<string, ReminderRequest> _requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<ReminderRequest, Notification?>> _builders = new(StringComparer.Ordinal);
    private readonly List<DateTimeOffset> _shown = [];
    private Timer? _timer;
    private EventHandler<bool>? _lockHandler;
    private bool _away;
    private DateTimeOffset? _returnedAt;

    /// <summary>Создаёт планировщик и читает очередь из прошлого запуска.</summary>
    public ReminderScheduler(INotificationService notifications, IUserActivityMonitor activity, IReminderStore store, Func<DateTimeOffset>? clock = null)
    {
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? (static () => DateTimeOffset.Now);
        foreach (var request in _store.Load())
        {
            _requests[request.Id] = request;
        }

        // Запуск — тоже возвращение к компьютеру: пропущенное, пока приложение было закрыто, покажется через пару минут.
        _returnedAt = _clock();
    }

    /// <summary>Удобные напоминания — не чаще.</summary>
    public TimeSpan MinInterval { get; init; } = TimeSpan.FromMinutes(60);

    /// <summary>Удобных напоминаний в день — не больше.</summary>
    public int MaxPerDay { get; init; } = 3;

    /// <summary>Простой ввода дольше — пользователь отошёл.</summary>
    public TimeSpan AwayThreshold { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Сколько поработать после возвращения, прежде чем напомнить.</summary>
    public TimeSpan ReturnSettle { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Сколько после возвращения момент остаётся хорошим.</summary>
    public TimeSpan GoodWindow { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Не дождались хорошего момента за это время после срока — напомнить, пока пользователь работает.</summary>
    public TimeSpan MaxWaitForGoodMoment { get; init; } = TimeSpan.FromHours(2);

    /// <summary>Простой ввода короче — пользователь за компьютером.</summary>
    public TimeSpan ActiveThreshold { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>«Утро» для «завтра» и «сегодня не напоминать».</summary>
    public TimeOnly MorningAt { get; init; } = new(9, 0);

    /// <summary>«Вечер» для «вечером».</summary>
    public TimeOnly EveningAt { get; init; } = new(18, 0);

    /// <summary>Заявки, ближайшие сверху.</summary>
    public IReadOnlyList<ReminderRequest> Pending => [.. _requests.Values.OrderBy(static request => request.Due)];

    /// <summary>Источник собирает уведомление своего вида в момент показа: свежие числа, свои кнопки. <c>null</c> — напоминать уже не о чем, заявка снимается.</summary>
    public void Register(string kind, Func<ReminderRequest, Notification?> build)
    {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        _builders[kind] = build ?? throw new ArgumentNullException(nameof(build));
    }

    /// <summary>Ставит заявку; заявка с тем же ключом заменяется, отсрочки начинаются заново.</summary>
    public void Schedule(ReminderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _requests[request.Id] = request;
        Save();
    }

    /// <summary>Есть такая заявка.</summary>
    public bool Contains(string id) => _requests.ContainsKey(id);

    /// <summary>Отложить без срока: 45 минут → 1,5 часа → 3 часа → следующее утро.</summary>
    public void Snooze(string id)
    {
        if (_requests.TryGetValue(id, out var request))
        {
            _requests[id] = Escalate(request, _clock());
            Save();
        }
    }

    /// <summary>Отложить до времени: «через час», «вечером», «завтра».</summary>
    public void SnoozeUntil(string id, DateTimeOffset due)
    {
        if (_requests.TryGetValue(id, out var request))
        {
            _requests[id] = request with { Due = due };
            Save();
        }
    }

    /// <summary>«Сегодня больше не напоминать»: до завтрашнего утра.</summary>
    public void SkipToday(string id) => SnoozeUntil(id, Tomorrow(_clock()));

    /// <summary>Сделано: заявка снимается, отсрочки сбрасываются.</summary>
    public void Complete(string id)
    {
        if (_requests.Remove(id))
        {
            Save();
        }
    }

    /// <summary>Кнопка «Позже»: показанное уже отложено по нарастающей, ответ ничего не меняет.</summary>
    public static NotificationAction LaterAction() => new(L.T("Reminders.Later"), static () => { });

    /// <summary>Кнопка «Сегодня не напоминать».</summary>
    public NotificationAction NotTodayAction(string id) => new(L.T("Reminders.NotToday"), () => SkipToday(id));

    /// <summary>Кнопка «Готово».</summary>
    public NotificationAction DoneAction(string id) => new(L.T("Reminders.Done"), () => Complete(id));

    /// <summary>«Вечером»: сегодня в <see cref="EveningAt"/>, а если вечер уже наступил — через час.</summary>
    public DateTimeOffset Evening(DateTimeOffset now)
    {
        var evening = At(now.LocalDateTime.Date, EveningAt);
        return evening > now ? evening : now.AddHours(1);
    }

    /// <summary>«Завтра»: следующее утро в <see cref="MorningAt"/>.</summary>
    public DateTimeOffset Tomorrow(DateTimeOffset now) => At(now.LocalDateTime.Date.AddDays(1), MorningAt);

    /// <summary>Сеанс заблокирован или разблокирован: разблокировка — возвращение к компьютеру.</summary>
    public void OnSessionLockChanged(bool locked)
    {
        if (locked)
        {
            _away = true;
        }
        else
        {
            _away = false;
            _returnedAt = _clock();
        }
    }

    /// <summary>Раз в минуту: показать одно напоминание, если пора и момент подходит.</summary>
    /// <returns>Показано напоминание.</returns>
    public bool Tick()
    {
        var now = _clock();
        var activity = _activity.Current;
        UpdatePresence(activity, now);
        if (activity.IsLocked || activity.Busy != UserBusyState.Free || _notifications.IsQuiet(now))
        {
            return false;
        }

        var due = _requests.Values
            .Where(request => request.Due <= now)
            .OrderBy(static request => request.Timing == ReminderTiming.Exact ? 0 : 1)
            .ThenBy(static request => request.Due)
            .ToList();
        foreach (var request in due)
        {
            if (request.Timing == ReminderTiming.Convenient && !CanShowConvenient(request, activity, now))
            {
                continue;
            }

            Notification? notification;
            if (_builders.TryGetValue(request.Kind, out var build))
            {
                notification = build(request);
            }
            else if (request.Title is { } title)
            {
                notification = new Notification(NotificationKind.Reminder, title, request.Text ?? string.Empty) { Actions = [DoneAction(request.Id), LaterAction()] };
            }
            else
            {
                // Источник этого вида ещё не зарегистрирован: заявка ждёт.
                continue;
            }

            if (notification is null)
            {
                Complete(request.Id);
                continue;
            }

            _requests[request.Id] = Escalate(request, now);
            if (request.Timing == ReminderTiming.Convenient)
            {
                _shown.Add(now);
            }

            Save();
            _notifications.Show(notification with { Key = "reminder:" + request.Id });
            return true;
        }

        return false;
    }

    /// <summary>Тикает раз в <paramref name="interval"/> на потоке интерфейса и слушает блокировку сеанса.</summary>
    public void Start(IDispatcher dispatcher, TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (_timer is not null)
        {
            return;
        }

        var period = interval ?? TimeSpan.FromMinutes(1);
        _lockHandler = (_, locked) => dispatcher.Post(() => OnSessionLockChanged(locked));
        _activity.SessionLockChanged += _lockHandler;
        _timer = new Timer(_ => dispatcher.Post(() => Tick()), null, period, period);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        if (_lockHandler is not null)
        {
            _activity.SessionLockChanged -= _lockHandler;
            _lockHandler = null;
        }
    }

    // Время суток — по часам компьютера.
    private static DateTimeOffset At(DateTime day, TimeOnly time)
    {
        var local = DateTime.SpecifyKind(day.Add(time.ToTimeSpan()), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    private ReminderRequest Escalate(ReminderRequest request, DateTimeOffset now)
    {
        var due = request.Snoozes < Delays.Length ? now + Delays[request.Snoozes] : Tomorrow(now);
        return request with { Due = due, Snoozes = request.Snoozes + 1 };
    }

    private bool CanShowConvenient(ReminderRequest request, UserActivity activity, DateTimeOffset now)
    {
        _shown.RemoveAll(time => now - time >= TimeSpan.FromDays(1));
        if (_shown.Count > 0 && now - _shown[^1] < MinInterval)
        {
            return false;
        }

        if (_shown.Count(time => time.LocalDateTime.Date == now.LocalDateTime.Date) >= MaxPerDay)
        {
            return false;
        }

        // После возвращения (и запуска) — сначала пара минут работы, в том числе для просроченного.
        var active = activity.IdleFor < ActiveThreshold;
        var settled = _returnedAt is not { } returned || now - returned >= ReturnSettle;
        var good = !_away && _returnedAt is { } back && settled && now - back <= GoodWindow;
        return active && settled && (good || now - request.Due >= MaxWaitForGoodMoment);
    }

    private void UpdatePresence(UserActivity activity, DateTimeOffset now)
    {
        if (activity.IsLocked || activity.Busy == UserBusyState.Away || activity.IdleFor >= AwayThreshold)
        {
            _away = true;
        }
        else if (_away && activity.IdleFor < ActiveThreshold)
        {
            _away = false;
            _returnedAt = now - activity.IdleFor;
        }
    }

    private void Save()
    {
        try
        {
            _store.Save([.. _requests.Values]);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Очередь останется в памяти до следующей удачной записи.
            _ = exception;
        }
    }
}

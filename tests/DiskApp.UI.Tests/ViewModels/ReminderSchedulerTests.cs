using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// «Умный момент» (PLAN.md, этапы 21.2 и 21.5, ADR-0013): подменные часы и сигналы занятости. Занятый пользователь
/// напоминаний не получает, освободившийся — один раз; хороший момент — возвращение после простоя и пара минут работы
/// или разблокировка; отсрочки нарастают до следующего утра; не чаще раза в час и не больше трёх в день; «Не
/// беспокоить» Windows и DiskApp держат показ; пропущенное, пока приложение было закрыто, показывается после запуска.
/// </summary>
public sealed class ReminderSchedulerTests
{
    private static readonly DateTimeOffset Start = new(new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Local));

    private readonly FakeActivity _activity = new();
    private readonly RecordingHost _host = new();
    private readonly MemoryReminderStore _store = new();
    private DateTimeOffset _now = Start;

    [Fact]
    public void Busy_ThenFree_RemindedOnceInGoodMoment()
    {
        // Arrange: заявка «в удобный момент» на сейчас, пользователь в полноэкранном приложении
        var (scheduler, _) = Create();
        scheduler.Schedule(new ReminderRequest("sorting", "test", Start));
        _activity.Busy = UserBusyState.FullScreen;

        // Act
        var shown = new List<bool>();
        for (var minute = 2; minute <= 10; minute++)
        {
            _now = Start.AddMinutes(minute);
            if (minute == 6)
            {
                _activity.Busy = UserBusyState.Free;
            }

            shown.Add(scheduler.Tick());
        }

        // Assert: пока полный экран — тишина; освободился — одно напоминание, дальше — отсрочка
        shown.Count(static value => value).ShouldBe(1);
        shown.IndexOf(true).ShouldBe(4);
        _host.Cards.ShouldHaveSingleItem().Title.ShouldBe("Напоминание test");
        scheduler.Pending.ShouldHaveSingleItem().Due.ShouldBe(Start.AddMinutes(6) + TimeSpan.FromMinutes(45));
    }

    [Fact]
    public void Snoozes_45Minutes_90Minutes_3Hours_ThenNextMorning_DoneResets()
    {
        // Arrange
        var (scheduler, _) = Create();
        scheduler.Schedule(new ReminderRequest("r", "test", Start));

        // Act
        var dues = new List<DateTimeOffset>();
        for (var i = 0; i < 4; i++)
        {
            scheduler.Snooze("r");
            dues.Add(scheduler.Pending[0].Due);
        }

        scheduler.Complete("r");
        scheduler.Schedule(new ReminderRequest("r", "test", Start));
        scheduler.Snooze("r");

        // Assert
        dues.ShouldBe([Start.AddMinutes(45), Start.AddMinutes(90), Start.AddHours(3), new DateTimeOffset(new DateTime(2026, 10, 16, 9, 0, 0, DateTimeKind.Local))]);
        scheduler.Pending.ShouldHaveSingleItem().Due.ShouldBe(Start.AddMinutes(45));
    }

    [Fact]
    public void Limits_OncePerHour_ThreePerDay_NextDayAgain()
    {
        // Arrange: пять заявок на сейчас, пользователь работает без перерыва — момент «через 2 часа после срока»
        var (scheduler, _) = Create();
        for (var i = 0; i < 5; i++)
        {
            scheduler.Schedule(new ReminderRequest($"r{i}", "test", Start));
        }

        // Act: тикаем раз в минуту до вечера
        var times = new List<DateTimeOffset>();
        for (_now = Start; _now < Start.AddHours(12); _now += TimeSpan.FromMinutes(1))
        {
            if (scheduler.Tick())
            {
                times.Add(_now);
            }
        }

        _now = new DateTimeOffset(new DateTime(2026, 10, 16, 10, 3, 0, DateTimeKind.Local));
        var nextDay = scheduler.Tick();

        // Assert: первое — через 2 минуты после запуска (запуск — возвращение), дальше не чаще раза в час, всего три
        times.Count.ShouldBe(3);
        times[0].ShouldBe(Start.AddMinutes(2));
        (times[1] - times[0]).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromHours(1));
        (times[2] - times[1]).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromHours(1));
        nextDay.ShouldBeTrue();
    }

    [Fact]
    public void DoNotDisturb_WindowsOrDiskApp_HoldsEvenExact()
    {
        // Arrange
        var (scheduler, notifications) = Create();
        scheduler.Schedule(new ReminderRequest("exact", "test", Start, ReminderTiming.Exact));

        // Act
        _activity.Busy = UserBusyState.DoNotDisturb;
        var windowsQuiet = scheduler.Tick();
        _activity.Busy = UserBusyState.Free;
        notifications.DoNotDisturbUntil = Start.AddHours(1);
        var appQuiet = scheduler.Tick();
        _activity.Busy = UserBusyState.Presentation;
        notifications.DoNotDisturbUntil = null;
        var presentation = scheduler.Tick();
        _activity.Busy = UserBusyState.Free;
        var free = scheduler.Tick();

        // Assert: точное напоминание не ждёт хорошего момента, но тишину соблюдает
        (windowsQuiet, appQuiet, presentation, free).ShouldBe((false, false, false, true));
    }

    [Fact]
    public void WindowsDoNotDisturbIgnored_WhenTurnedOffInSettings_PresentationStillHolds()
    {
        // Arrange
        var (scheduler, notifications) = Create();
        notifications.RespectWindowsDoNotDisturb = false;
        scheduler.Schedule(new ReminderRequest("a", "test", Start, ReminderTiming.Exact));
        scheduler.Schedule(new ReminderRequest("b", "test", Start, ReminderTiming.Exact));

        // Act
        _activity.Busy = UserBusyState.Presentation;
        var presentation = scheduler.Tick();
        _activity.Busy = UserBusyState.DoNotDisturb;
        var windowsQuiet = scheduler.Tick();

        // Assert
        (presentation, windowsQuiet).ShouldBe((false, true));
    }

    [Fact]
    public void MissedWhileClosed_ShownAfterStart()
    {
        // Arrange: прошлый запуск оставил просроченные заявки
        _store.Save(
        [
            new ReminderRequest("exact", "test", Start.AddDays(-1), ReminderTiming.Exact) { Snoozes = 1 },
            new ReminderRequest("soon", "test", Start.AddHours(-3)),
        ]);
        var (scheduler, _) = Create();

        // Act
        var first = scheduler.Tick();
        _now = Start.AddMinutes(1);
        var tooEarly = scheduler.Tick();
        _now = Start.AddMinutes(2);
        var settled = scheduler.Tick();

        // Assert: точное — сразу, удобное — после пары минут работы
        (first, tooEarly, settled).ShouldBe((true, false, true));
        _host.Cards.Select(static card => card.Notification.Key).ShouldBe(["reminder:exact", "reminder:soon"]);
    }

    [Fact]
    public void GoodMoment_ReturnAfterIdleOrUnlock_NotRightAway()
    {
        // Arrange: напоминание на 10:30; пользователь работает, в 10:31 отходит
        var (scheduler, _) = Create();
        _now = Start.AddMinutes(20);
        scheduler.Tick();
        scheduler.Schedule(new ReminderRequest("r", "test", Start.AddMinutes(30)));

        // Act
        var results = new List<(string, bool)>();
        Step(31, TimeSpan.Zero, "работает после срока");
        Step(45, TimeSpan.FromMinutes(14), "отошёл");
        Step(46, TimeSpan.FromSeconds(5), "вернулся");
        Step(47, TimeSpan.FromSeconds(3), "минута работы");
        Step(48, TimeSpan.FromSeconds(2), "две минуты работы");

        // Assert
        results.ShouldBe([("работает после срока", false), ("отошёл", false), ("вернулся", false), ("минута работы", false), ("две минуты работы", true)]);

        // Разблокировка — тоже возвращение: следующая заявка — через пару минут после неё, если прошёл час
        scheduler.Schedule(new ReminderRequest("next", "test", Start.AddMinutes(60)));
        _now = Start.AddHours(2);
        scheduler.OnSessionLockChanged(true);
        _activity.Locked = true;
        var locked = scheduler.Tick();
        _activity.Locked = false;
        scheduler.OnSessionLockChanged(false);
        _activity.Idle = TimeSpan.Zero;
        var justUnlocked = scheduler.Tick();
        _now = Start.AddHours(2).AddMinutes(2);
        var afterUnlock = scheduler.Tick();
        (locked, justUnlocked, afterUnlock).ShouldBe((false, false, true));

        void Step(int minute, TimeSpan idle, string label)
        {
            _now = Start.AddMinutes(minute);
            _activity.Idle = idle;
            results.Add((label, scheduler.Tick()));
        }
    }

    [Fact]
    public void Builder_NothingToRemind_RequestRemoved_ActionsReschedule()
    {
        // Arrange
        var (scheduler, _) = Create();
        var files = 0;
        scheduler.Register("sorting", request => files == 0 ? null : new Notification(NotificationKind.Sorting, $"{files} файлов к разбору", string.Empty)
        {
            Actions = [scheduler.NotTodayAction(request.Id), ReminderScheduler.LaterAction()],
        });
        scheduler.Schedule(new ReminderRequest("sorting", "sorting", Start, ReminderTiming.Exact));

        // Act
        var empty = scheduler.Tick();
        var removed = !scheduler.Contains("sorting");
        files = 7;
        scheduler.Schedule(new ReminderRequest("sorting", "sorting", Start, ReminderTiming.Exact));
        scheduler.Tick();
        _host.Cards[^1].Actions[0].Command.Execute(null);

        // Assert
        (empty, removed).ShouldBe((false, true));
        _host.Cards[^1].Title.ShouldBe("7 файлов к разбору");
        scheduler.Pending.ShouldHaveSingleItem().Due.ShouldBe(new DateTimeOffset(new DateTime(2026, 10, 16, 9, 0, 0, DateTimeKind.Local)));
        _store.Load().ShouldHaveSingleItem().Due.ShouldBe(scheduler.Pending[0].Due);
    }

    private (ReminderScheduler Scheduler, NotificationService Notifications) Create()
    {
        var notifications = new NotificationService(_host, clock: () => _now) { MaxCardsPerHour = 100, RepeatInterval = TimeSpan.Zero };
        var scheduler = new ReminderScheduler(notifications, _activity, _store, () => _now);
        scheduler.Register("test", static request => new Notification(NotificationKind.Reminder, "Напоминание test", request.Id));
        return (scheduler, notifications);
    }

    private sealed class FakeActivity : IUserActivityMonitor
    {
        public TimeSpan Idle { get; set; }

        public UserBusyState Busy { get; set; }

        public bool Locked { get; set; }

        public UserActivity Current => new(Idle, Busy, Locked);

        public event EventHandler<bool>? SessionLockChanged
        {
            add { }
            remove { }
        }
    }

    private sealed class RecordingHost : INotificationHost
    {
        public List<NotificationCardViewModel> Cards { get; } = [];

        public bool IsMainWindowVisible => false;

        public void ShowCard(NotificationCardViewModel card) => Cards.Add(card);
    }
}

using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Напоминание о разборе (PLAN.md, этап 21.3, вопрос 13): одна заявка на все накопители — новые файлы через 2 часа,
/// десять неразобранных или давние файлы — сразу, вечером в 18:00; текст со свежими числами («3 новых файла с утра»),
/// «Разобрать» открывает «Разбор», «Сегодня не напоминать» — до утра; разобрано — заявки нет, «оставленные здесь» не в счёт.
/// </summary>
public sealed class SortingRemindersTests
{
    private const string Downloads = @"C:\Users\user\Downloads";

    private static readonly DateTimeOffset Morning = new(new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Local));

    private readonly SampleSortingSettings _settings = new(new SortingFolders([new SortingFolderInfo(Downloads, "Загрузки")], []));
    private readonly IInboxService _inbox = Substitute.For<IInboxService>();
    private readonly ILibraryService _library = Substitute.For<ILibraryService>();
    private readonly IAppCommands _commands = Substitute.For<IAppCommands>();
    private readonly List<NotificationCardViewModel> _cards = [];
    private DateTimeOffset _now = Morning;

    public SortingRemindersTests()
    {
        _library.GetTagsAsync(Arg.Any<CancellationToken>()).Returns([]);
        _library.GetAutoTagsAsync(Arg.Any<CancellationToken>()).Returns([]);
    }

    [Fact]
    public async Task NewFiles_InTwoHours_TodayAllNew_SortOpensPage()
    {
        // Arrange: накопитель пуст, за полчаса пришли три файла
        var (_, scheduler, page) = await CreateAsync();
        _now = Morning.AddMinutes(30);

        // Act
        await ReloadAsync(page, [At("акт.pdf", Morning.AddMinutes(10)), At("фото.jpg", Morning.AddMinutes(20)), At("смета.xlsx", Morning.AddMinutes(25))]);
        var due = scheduler.Pending.ShouldHaveSingleItem().Due;
        _now = due + TimeSpan.FromMinutes(1);
        var shown = scheduler.Tick();
        _cards[0].Actions[0].Command.Execute(null);

        // Assert
        due.ShouldBe(Morning.AddMinutes(10).AddHours(2));
        shown.ShouldBeTrue();
        (_cards[0].Title, _cards[0].Text).ShouldBe(("Пора разобрать накопители", "3 новых файла с утра"));
        _cards[0].Actions.Select(static action => action.Text).ShouldBe(["Разобрать", "Позже", "Сегодня не напоминать"]);
        _commands.Received(1).NavigateTo(PageKeys.Inbox);
        scheduler.Pending.ShouldBeEmpty();
        page.BadgeValue.ShouldBe(3);
    }

    [Fact]
    public async Task TenWaitingOrStale_Now_EveningOtherwise_KeptNotCounted()
    {
        // Arrange: пусто; пришли девять новых и один «оставленный здесь» — до порога не хватает
        var (_, scheduler, page) = await CreateAsync();
        _now = Morning.AddMinutes(30);
        var items = Enumerable.Range(1, 9).Select(i => At($"{i}.pdf", Morning.AddMinutes(i))).Append(At("оставить.exe", Morning.AddMinutes(1), InboxItemState.Kept)).ToArray();

        // Act
        await ReloadAsync(page, items);
        var nine = scheduler.Pending.Single().Due;
        await ReloadAsync(page, [.. items, Item("старый.docx", 24 * 4)]);
        var stale = scheduler.Pending.Single().Due;

        // Assert: девять — через 2 часа после первого; десятый, к тому же давний, — сейчас
        nine.ShouldBe(Morning.AddMinutes(1).AddHours(2));
        stale.ShouldBe(_now);
        page.BadgeValue.ShouldBe(10);
    }

    [Fact]
    public async Task Evening_WhenNothingNew_ThenNotToday_UntilMorning()
    {
        // Arrange: файл со вчера, новых нет
        var (reminders, scheduler, _) = await CreateAsync(Item("вчерашний.pdf", 20));
        reminders.Evaluate();

        // Act: вечером напоминание приходит, «Сегодня не напоминать» откладывает до утра и новые поводы его не торопят
        var evening = new DateTimeOffset(new DateTime(2026, 10, 15, 18, 0, 0, DateTimeKind.Local));
        var dueEvening = scheduler.Pending.Single().Due;
        _now = evening.AddMinutes(1);
        scheduler.Tick();
        var text = _cards[^1].Text;
        _cards[^1].Actions[2].Command.Execute(null);
        reminders.Evaluate();

        // Assert
        dueEvening.ShouldBe(evening);
        text.ShouldBe("1 файл ждёт разбора");
        scheduler.Pending.Single().Due.ShouldBe(new DateTimeOffset(new DateTime(2026, 10, 16, 9, 0, 0, DateTimeKind.Local)));
    }

    [Fact]
    public async Task AllSorted_RequestRemoved()
    {
        // Arrange
        var (reminders, scheduler, page) = await CreateAsync(Item("акт.pdf", 1));
        reminders.Evaluate();

        // Act: файл разобрали — бейдж обнулился
        await ReloadAsync(page, []);

        // Assert
        scheduler.Pending.ShouldBeEmpty();
    }

    [Fact]
    public async Task TurnedOffInSettings_NoRequest()
    {
        // Arrange
        var (_, scheduler, page) = await CreateAsync(turnedOff: true);

        // Act
        await ReloadAsync(page, [At("акт.pdf", Morning.AddMinutes(5)), Item("старый.pdf", 24 * 5)]);

        // Assert
        scheduler.Pending.ShouldBeEmpty();
    }

    private static InboxEntry Item(string name, int hoursAgo, InboxItemState state = InboxItemState.New) => At(name, Morning.AddHours(-hoursAgo), state);

    private static InboxEntry At(string name, DateTimeOffset modified, InboxItemState state = InboxItemState.New)
        => new(new FileEntry { FullPath = Path.Combine(Downloads, name), Kind = FileKind.Document, Root = "user", Size = 100, Modified = modified }, Downloads, state, true);

    private async Task ReloadAsync(SortingViewModel page, InboxEntry[] items)
    {
        _inbox.GetAsync(Arg.Any<CancellationToken>()).Returns(new InboxSnapshotInfo([new InboxFolderState(Downloads, null)], items, null));
        await page.RefreshAsync();
    }

    private async Task<(SortingReminders Reminders, ReminderScheduler Scheduler, SortingViewModel Page)> CreateAsync(params InboxEntry[] items) => await CreateAsync(false, items);

    private async Task<(SortingReminders Reminders, ReminderScheduler Scheduler, SortingViewModel Page)> CreateAsync(bool turnedOff, params InboxEntry[] items)
    {
        _inbox.GetAsync(Arg.Any<CancellationToken>()).Returns(new InboxSnapshotInfo([new InboxFolderState(Downloads, null)], items, null));
        var page = new SortingViewModel(
            _settings,
            Substitute.For<IBrowseService>(),
            new SampleOperationsService(),
            Substitute.For<ISearchService>(),
            _library,
            Substitute.For<IRootsService>(),
            Substitute.For<IDialogService>(),
            Substitute.For<IInfoBarService>(),
            () => _now,
            _inbox);
        var host = Substitute.For<INotificationHost>();
        host.When(static h => h.ShowCard(Arg.Any<NotificationCardViewModel>())).Do(call => _cards.Add(call.Arg<NotificationCardViewModel>()));
        var notifications = new NotificationService(host, clock: () => _now);
        if (turnedOff)
        {
            notifications.Apply(NotificationPreferences.From(new DiskApp.Contracts.App.NotificationSettingsDto { SortingReminders = false }));
        }

        var scheduler = new ReminderScheduler(notifications, new NullUserActivityMonitor(), new MemoryReminderStore(), () => _now)
        {
            // Пользователь не отходит от компьютера: удобный момент — через 2 часа после срока; для теста — сразу.
            MaxWaitForGoodMoment = TimeSpan.Zero,
        };
        var reminders = new SortingReminders(scheduler, page, _commands, () => _now, notifications);
        await page.RefreshAsync();
        return (reminders, scheduler, page);
    }
}

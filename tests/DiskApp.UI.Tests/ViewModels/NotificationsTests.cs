using DiskApp.Contracts.Hub;
using DiskApp.Contracts.Roots;
using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Канал уведомлений (PLAN.md, этап 21.1, ADR-0013): нарастание «полоса главного окна → тихая карточка → подсказка в
/// трее», общие лимиты (одно событие не чаще 10 минут, не больше 4 карточек в час, срочное — без лимита), «не беспокоить»
/// и часы тишины, непрочитанное; источники — недоступный корень и ошибка индекса, долгая и неудачная операция.
/// </summary>
public sealed class NotificationsTests
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 15, 12, 0, 0, TimeSpan.FromHours(3));

    private readonly RecordingHost _host = new();
    private readonly IInfoBarService _infoBar = Substitute.For<IInfoBarService>();
    private DateTimeOffset _now = Noon;

    [Fact]
    public void MainWindowOpen_GoesToInfoBarWithFirstAction_NotUnread()
    {
        // Arrange
        _host.MainVisible = true;
        var service = Service();
        var undone = false;

        // Act
        var route = service.Show(new Notification(NotificationKind.AutoSort, "Авторазбор", "2 файла → 417")
        {
            Severity = FAInfoBarSeverity.Success,
            Actions = [new NotificationAction("Отменить", () => undone = true), new NotificationAction("Ещё", static () => { })],
        });

        // Assert
        route.ShouldBe(NotificationRoute.InfoBar);
        _infoBar.Received(1).Show("Авторазбор", "2 файла → 417", FAInfoBarSeverity.Success, "Отменить", Arg.Any<Action>());
        _infoBar.ReceivedCalls().Single().GetArguments()[4].ShouldBeOfType<Action>().Invoke();
        undone.ShouldBeTrue();
        _host.Cards.ShouldBeEmpty();
        service.UnreadCount.ShouldBe(0);
    }

    [Fact]
    public void WindowHidden_NormalIsCard_QuietOnlyTray_UnreadUntilWindowOpened()
    {
        // Arrange
        var service = Service();
        var changes = 0;
        service.Changed += (_, _) => changes++;

        // Act
        var card = service.Show(new Notification(NotificationKind.Operation, "Готово", "Копирование «отчёт.pdf»"));
        var quiet = service.Show(new Notification(NotificationKind.Index, "Папка снова доступна", "«TCI2»") { Importance = NotificationImportance.Quiet });
        var unread = (service.UnreadCount, service.LatestUnread!.Notification.Title);
        service.MarkAllRead();

        // Assert
        (card, quiet).ShouldBe((NotificationRoute.Card, NotificationRoute.Tray));
        _host.Cards.ShouldHaveSingleItem().Title.ShouldBe("Готово");
        unread.ShouldBe((2, "Папка снова доступна"));
        service.UnreadCount.ShouldBe(0);
        service.LatestUnread.ShouldBeNull();
        service.Recent.Select(static record => record.Route).ShouldBe([NotificationRoute.Tray, NotificationRoute.Card]);
        changes.ShouldBe(3);
    }

    [Fact]
    public void SameEvent_CardAtMostEveryTenMinutes()
    {
        // Arrange
        var service = Service();
        var offline = new Notification(NotificationKind.Index, "Папка недоступна", "«TCI2»") { Key = "root-offline:2" };

        // Act
        var first = service.Show(offline);
        _now = Noon.AddMinutes(9);
        var repeated = service.Show(offline);
        var other = service.Show(offline with { Key = "root-offline:3" });
        _now = Noon.AddMinutes(10);
        var later = service.Show(offline);

        // Assert
        new[] { first, repeated, other, later }.ShouldBe([NotificationRoute.Card, NotificationRoute.Tray, NotificationRoute.Card, NotificationRoute.Card]);
    }

    [Fact]
    public void CardsPerHour_Limited_UrgentStillShown_HourLaterAgain()
    {
        // Arrange
        var service = Service();
        var routes = new List<NotificationRoute>();

        // Act
        for (var i = 0; i < 5; i++)
        {
            _now = Noon.AddMinutes(i);
            routes.Add(service.Show(new Notification(NotificationKind.Operation, $"Готово {i}", string.Empty)));
        }

        var urgent = service.Show(new Notification(NotificationKind.Operation, "Операция не выполнена", "Диск полон") { Importance = NotificationImportance.Urgent });
        _now = Noon.AddMinutes(61);
        var hourLater = service.Show(new Notification(NotificationKind.Operation, "Готово 5", string.Empty));

        // Assert
        routes.ShouldBe([NotificationRoute.Card, NotificationRoute.Card, NotificationRoute.Card, NotificationRoute.Card, NotificationRoute.Tray]);
        urgent.ShouldBe(NotificationRoute.Card);
        hourLater.ShouldBe(NotificationRoute.Card);
    }

    [Fact]
    public void DoNotDisturbAndQuietHours_OnlyTray_EvenUrgent()
    {
        // Arrange
        var service = Service();
        var urgent = new Notification(NotificationKind.Index, "Ошибка индексации", "«TCI2»") { Importance = NotificationImportance.Urgent };

        // Act
        service.DoNotDisturbUntil = Noon.AddHours(1);
        var dnd = service.Show(urgent);
        service.DoNotDisturbUntil = null;
        service.QuietHours = (new TimeOnly(22, 0), new TimeOnly(8, 0));
        _now = new DateTimeOffset(new DateTime(2026, 10, 15, 23, 30, 0, DateTimeKind.Local));
        var night = service.Show(urgent);
        _now = new DateTimeOffset(new DateTime(2026, 10, 16, 7, 59, 0, DateTimeKind.Local));
        var morning = service.Show(urgent with { Key = "x" });
        _now = new DateTimeOffset(new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Local));
        var day = service.Show(urgent with { Key = "y" });

        // Assert
        (dnd, night, morning, day).ShouldBe((NotificationRoute.Tray, NotificationRoute.Tray, NotificationRoute.Tray, NotificationRoute.Card));
    }

    [Fact]
    public void Card_ThreeActionsAtMost_ActionClosesAndRuns_UrgentStaysLonger()
    {
        // Arrange
        var ran = new List<string>();
        var card = new NotificationCardViewModel(new Notification(NotificationKind.General, "Заголовок", "Текст")
        {
            Severity = FAInfoBarSeverity.Error,
            Importance = NotificationImportance.Urgent,
            Actions = [new("A", () => ran.Add("A")), new("B", () => ran.Add("B")), new("C", () => ran.Add("C")), new("D", () => ran.Add("D"))],
        });
        var closed = 0;
        card.CloseRequested += (_, _) => closed++;

        // Act
        card.Actions[1].Command.Execute(null);
        card.CloseCommand.Execute(null);

        // Assert
        card.Actions.Select(static action => action.Text).ShouldBe(["A", "B", "C"]);
        ran.ShouldBe(["B"]);
        closed.ShouldBe(2);
        (card.IsError, card.AutoCloseAfter).ShouldBe((true, TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public void Index_OnlyChangesDuringSession_OfflineErrorAndBack()
    {
        // Arrange: «Флешка» отключена ещё до запуска — это точка отсчёта, а не новость
        var service = Service();
        var commands = Substitute.For<IAppCommands>();
        var index = new IndexNotifications(service, commands);
        index.Apply(Status((1, "TCI2", RootStatus.Online), (2, "Флешка", RootStatus.Offline)));

        // Act
        index.Apply(new RootStatusChangedDto(1, RootStatus.Offline, null));
        index.Apply(new RootStatusChangedDto(1, RootStatus.Offline, "Нет сети"));
        index.Apply(new RootStatusChangedDto(1, RootStatus.Scanning, null));
        index.Apply(new RootStatusChangedDto(1, RootStatus.Online, null));
        index.Apply(new RootStatusChangedDto(1, RootStatus.Error, "Нет доступа"));
        index.Apply(new RootStatusChangedDto(2, RootStatus.Online, null));
        _host.Cards[0].Actions.ShouldHaveSingleItem().Command.Execute(null);

        // Assert: «Флешка» вернулась — тихо, как и TCI2; повтор «недоступна» не пишется
        service.Recent.Reverse().Select(static record => (record.Notification.Title, record.Notification.Text, record.Route)).ShouldBe(
        [
            ("Папка недоступна", "«TCI2»: диск отключён или нет сети. Индекс сохранён, поиск находит файлы.", NotificationRoute.Card),
            ("Папка снова доступна", "«TCI2»", NotificationRoute.Tray),
            ("Ошибка индексации", "«TCI2»: Нет доступа", NotificationRoute.Card),
            ("Папка снова доступна", "«Флешка»", NotificationRoute.Tray),
        ]);
        commands.Received(1).ShowMainWindow();
        commands.Received(1).NavigateTo(PageKeys.Home);
    }

    [Fact]
    public async Task Operations_LongDoneIsCardWithUndo_ShortOnlyTray_FailedUrgent()
    {
        // Arrange: окно скрыто — операцию начали из окна поиска
        var notifications = Service();
        var operations = new SampleOperationsService();
        var page = new OperationsViewModel(operations, new ImmediateDispatcher(), notifications: notifications);
        var slow = await page.EnqueueAsync(new NewOperation(FileOperationKind.Copy, [@"D:\видео.mp4"], @"E:\Архив"));
        var quick = await page.EnqueueAsync(new NewOperation(FileOperationKind.Rename, [@"D:\a.txt"], null, "b.txt"));
        var broken = await page.EnqueueAsync(new NewOperation(FileOperationKind.Move, [@"D:\c.txt"], @"E:\"));

        // Act
        operations.Report(slow! with { State = FileOperationState.Completed, CanUndo = true, Completed = slow.Created + TimeSpan.FromSeconds(40) });
        operations.Report(quick! with { State = FileOperationState.Completed, CanUndo = true, Completed = quick.Created + TimeSpan.FromSeconds(1) });
        operations.Report(broken! with { State = FileOperationState.Failed, Message = "Диск полон", Completed = broken.Created });

        // Assert
        notifications.Recent.Reverse().Select(static record => (record.Notification.Title, record.Route, record.Notification.Importance)).ShouldBe(
        [
            ("Готово", NotificationRoute.Card, NotificationImportance.Normal),
            ("Готово", NotificationRoute.Tray, NotificationImportance.Quiet),
            ("Операция не выполнена", NotificationRoute.Card, NotificationImportance.Urgent),
        ]);
        _host.Cards[0].Actions.ShouldHaveSingleItem().Text.ShouldBe("Отменить");
        _host.Cards[1].Text.ShouldEndWith(": Диск полон");
    }

    private static HubStatusDto Status(params (long Id, string Label, RootStatus Status)[] roots)
        => new(new IndexStatusDto(IndexerState.Idle, false, [.. roots.Select(static root => new RootIndexStatusDto(root.Id, @"D:\" + root.Label, root.Label, root.Status, null, null, false, null, null))]), []);

    private NotificationService Service() => new(_host, _infoBar, () => _now);

    private sealed class RecordingHost : INotificationHost
    {
        public bool MainVisible { get; set; }

        public List<NotificationCardViewModel> Cards { get; } = [];

        public bool IsMainWindowVisible => MainVisible;

        public void ShowCard(NotificationCardViewModel card) => Cards.Add(card);
    }
}

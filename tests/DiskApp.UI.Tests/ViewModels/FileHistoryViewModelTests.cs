using DiskApp.Contracts.History;
using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// История файлов в интерфейсе (PLAN.md, этап 20.4): лента по дням с текстами событий и источником, отбор по папке,
/// виду, источнику и имени, страницы, «Вернуть на место» и «Восстановить», обновление по новым записям только на
/// открытой странице; «История» в карточке файла читается при раскрытии и следует за выбором; отметка «Разбор» у операции.
/// </summary>
public sealed class FileHistoryViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 15, 0, 0, TimeSpan.FromHours(3));

    private readonly IInfoBarService _notices = Substitute.For<IInfoBarService>();

    [Fact]
    public async Task Feed_GroupsByDayNewestFirst_DescribesEventsAndSources()
    {
        // Arrange
        var history = new SampleFileHistoryService(
        [
            Event(Now.AddDays(-3), FileEventKind.Created, FileEventSource.Explorer, @"C:\Загрузки\акт.pdf"),
            Event(Now.AddDays(-1), FileEventKind.Moved, FileEventSource.Sorting, @"D:\417\Акты\акт.pdf", @"C:\Загрузки\акт.pdf"),
            Event(Now.AddHours(-2), FileEventKind.Renamed, FileEventSource.Explorer, @"D:\417\Акты\акт сверки.pdf", @"D:\417\Акты\акт.pdf"),
            Event(Now.AddHours(-1), FileEventKind.TagsChanged, FileEventSource.DiskApp, @"D:\417\Акты\акт сверки.pdf", details: "+417, −черновик"),
            Event(Now.AddMinutes(-5), FileEventKind.Recycled, FileEventSource.Explorer, @"D:\417\старое.docx"),
        ]);
        var feed = Feed(history);

        // Act
        await feed.ActivateAsync();

        // Assert
        feed.Days.Select(static day => day.Title).ShouldBe(["Сегодня", "Вчера", "11 окт"]);
        var today = feed.Days[0].Items;
        today.Select(static item => (item.Name, item.ActionText, item.SourceText)).ShouldBe(
        [
            ("старое.docx", "Удалён в корзину из «417»", "Проводник"),
            ("акт сверки.pdf", "Теги: +417, −черновик", "DiskApp"),
            ("акт сверки.pdf", "Переименован, было «акт.pdf»", "Проводник"),
        ]);
        today.Select(static item => (item.CanRevert, item.RevertText)).ShouldBe([(true, "Восстановить"), (false, "Вернуть на место"), (true, "Вернуть на место")]);
        var moved = feed.Days[1].Items.ShouldHaveSingleItem();
        (moved.ActionText, moved.SourceText).ShouldBe(("Перенесён: «Загрузки» → «Акты»", "Разбор"));
        feed.Days[2].Items.ShouldHaveSingleItem().ActionText.ShouldBe("Появился в «Загрузки»");
        feed.IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public async Task Feed_Filters_GoToServer_ClearResets()
    {
        // Arrange
        var history = new SampleFileHistoryService([Event(Now, FileEventKind.Moved, FileEventSource.Explorer, @"D:\417\акт.pdf", @"C:\Загрузки\акт.pdf")]);
        var roots = Substitute.For<IRootsService>();
        roots.GetRootsAsync(Arg.Any<CancellationToken>()).Returns([new RootInfo { Id = 1, Label = "Работа", Path = @"D:\417" }]);
        var feed = Feed(history, roots);
        await feed.ActivateAsync();

        // Act
        feed.SelectedFolder = feed.Folders.Single(static option => option.Value == @"D:\417");
        feed.SelectedKind = FileHistoryFeedViewModel.KindFilters[1];
        feed.SelectedSource = FileHistoryFeedViewModel.SourceFilters[2];
        feed.SearchText = "  было  ";
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var filtered = history.Requests[^1];
        var nothing = feed.IsEmpty;
        await feed.ClearFiltersCommand.ExecuteAsync(null);

        // Assert
        (filtered.Folder, filtered.Text).ShouldBe((@"D:\417", "было"));
        filtered.Kinds.ShouldBe([FileEventKind.Moved, FileEventKind.Renamed]);
        filtered.Sources.ShouldBe([FileEventSource.Sorting, FileEventSource.AutoSort]);
        nothing.ShouldBeTrue();
        feed.IsFiltered.ShouldBeFalse();
        history.Requests[^1].ShouldBe(new FileHistoryFilter { Limit = FileHistoryFeedViewModel.PageSize });
        feed.Days.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Feed_LoadMore_NextPageUntilEnd()
    {
        // Arrange: полторы страницы
        var history = new SampleFileHistoryService(Enumerable.Range(0, 150).Select(i => Event(Now.AddMinutes(-i), FileEventKind.Created, FileEventSource.Explorer, $@"D:\{i}.txt")));
        var feed = Feed(history);
        await feed.ActivateAsync();
        var first = feed.Days.Sum(static day => day.Items.Count);

        // Act
        await feed.LoadMoreCommand.ExecuteAsync(null);

        // Assert
        first.ShouldBe(FileHistoryFeedViewModel.PageSize);
        feed.Days.Sum(static day => day.Items.Count).ShouldBe(150);
        feed.HasMore.ShouldBeFalse();
    }

    [Fact]
    public async Task Feed_Revert_ShowsResultAndNewRecord_FailureExplained()
    {
        // Arrange
        var history = new SampleFileHistoryService(
        [
            Event(Now.AddMinutes(-2), FileEventKind.Moved, FileEventSource.Explorer, @"D:\417\акт.pdf", @"C:\Загрузки\акт.pdf"),
            Event(Now.AddMinutes(-1), FileEventKind.Recycled, FileEventSource.DiskApp, @"D:\план.docx"),
        ]);
        var feed = Feed(history);
        await feed.ActivateAsync();
        var items = feed.Days[0].Items;

        // Act
        await items.Single(static item => item.Event.Kind == FileEventKind.Moved).RevertCommand.ExecuteAsync(null);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        history.FailRevertWith = "Место занято: D:\\план.docx";
        await items.Single(static item => item.Event.Kind == FileEventKind.Recycled).RevertCommand.ExecuteAsync(null);

        // Assert: возврат записан новым событием — лента перечиталась сама
        _notices.Received(1).Show("Возвращается на место", "акт.pdf", FAInfoBarSeverity.Success);
        _notices.Received(1).Show("Не удалось вернуть", "Место занято: D:\\план.docx", FAInfoBarSeverity.Error);
        feed.Days[0].Items.Count.ShouldBe(3);
        feed.Days[0].Items[0].Event.Path.ShouldBe(@"C:\Загрузки\акт.pdf");
    }

    [Fact]
    public async Task Feed_NewRecords_ReloadOnlyWhileOpen()
    {
        // Arrange
        var history = new SampleFileHistoryService();
        var feed = Feed(history);
        await feed.ActivateAsync();

        // Act: запись на открытой странице, затем на закрытой
        history.Add(Event(Now, FileEventKind.Created, FileEventSource.Explorer, @"D:\a.txt"));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var whileOpen = feed.Days.Sum(static day => day.Items.Count);
        feed.Deactivate();
        var requests = history.Requests.Count;
        history.Add(Event(Now, FileEventKind.Created, FileEventSource.Explorer, @"D:\b.txt"));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var whileClosed = history.Requests.Count - requests;
        await feed.ActivateAsync();

        // Assert
        whileOpen.ShouldBe(1);
        whileClosed.ShouldBe(0);
        feed.Days.Sum(static day => day.Items.Count).ShouldBe(2);
    }

    [Fact]
    public async Task Panel_LoadsWhenExpanded_FollowsPathAcrossMoves()
    {
        // Arrange
        var history = new SampleFileHistoryService(
        [
            Event(Now.AddDays(-1), FileEventKind.Moved, FileEventSource.Sorting, @"D:\417\акт.pdf", @"C:\Загрузки\акт.pdf"),
            Event(Now.AddHours(-1), FileEventKind.Renamed, FileEventSource.Explorer, @"D:\417\акт сверки.pdf", @"D:\417\акт.pdf"),
            Event(Now, FileEventKind.Created, FileEventSource.Explorer, @"D:\другой.pdf"),
        ]);
        var panel = new FileHistoryPanelViewModel(history, _notices, new ImmediateDispatcher());

        // Act
        panel.Path = @"D:\417\акт сверки.pdf";
        await panel.Loading;
        var collapsed = panel.Items.Count;
        panel.IsExpanded = true;
        await panel.Loading;
        var chain = panel.Items.Select(static item => item.Event.Kind).ToList();
        panel.Path = @"D:\другой.pdf";
        await panel.Loading;

        // Assert
        collapsed.ShouldBe(0);
        chain.ShouldBe([FileEventKind.Renamed, FileEventKind.Moved]);
        panel.Items.ShouldHaveSingleItem().Event.Kind.ShouldBe(FileEventKind.Created);
        panel.IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public async Task HistoryPage_ActivatesFeedOnNavigation_SortingOperationMarked()
    {
        // Arrange
        var history = new SampleFileHistoryService([Event(Now, FileEventKind.Created, FileEventSource.Explorer, @"D:\a.txt")]);
        var page = new OperationsViewModel(new SampleOperationsService(), new ImmediateDispatcher(), notices: _notices, history: history);

        // Act
        page.OnNavigatedTo();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        var title = OperationItemViewModel.Describe(new OperationInfo
        {
            Id = Guid.NewGuid(),
            Kind = FileOperationKind.Move,
            Sources = [@"C:\Загрузки\акт.pdf"],
            Destination = @"D:\417\Акты",
            IsSorting = true,
        });

        // Assert
        page.Title.ShouldBe("История");
        page.HasHistory.ShouldBeTrue();
        page.History!.Days.ShouldHaveSingleItem();
        title.ShouldBe("Разбор: «акт.pdf» в «Акты»");
    }

    private static FileHistoryEvent Event(DateTimeOffset time, FileEventKind kind, FileEventSource source, string path, string? oldPath = null, string? details = null)
        => new(0, time, kind, source, path, oldPath, false, 10, details, kind is FileEventKind.Moved or FileEventKind.Renamed or FileEventKind.Recycled);

    private FileHistoryFeedViewModel Feed(IFileHistoryService history, IRootsService? roots = null)
        => new(history, new ImmediateDispatcher(), roots, _notices, static () => Now) { ReloadDelay = TimeSpan.Zero };
}

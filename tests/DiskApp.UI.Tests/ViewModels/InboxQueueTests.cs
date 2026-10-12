using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Очередь накопителей на странице «Разбор» (PLAN.md, этап 17.1): дописываемые файлы не предлагаются, отложенные — в конце
/// и не в бейдже, «оставленные здесь» — по кнопке, состояния меняются из меню, очередь обновляется сама и не сбрасывает
/// выделение, подпапки включаются у накопителя.
/// </summary>
public sealed class InboxQueueTests
{
    private const string Downloads = @"C:\Users\user\Downloads";

    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.FromHours(3));

    private readonly SampleSortingSettings _settings = new(new SortingFolders([new SortingFolderInfo(Downloads, "Загрузки")], []));
    private readonly IInboxService _inbox = Substitute.For<IInboxService>();
    private readonly ILibraryService _library = Substitute.For<ILibraryService>();

    public InboxQueueTests()
    {
        _library.GetTagsAsync(Arg.Any<CancellationToken>()).Returns([]);
        _library.GetAutoTagsAsync(Arg.Any<CancellationToken>()).Returns([]);
    }

    [Fact]
    public async Task Queue_PendingHidden_PostponedLast_KeptOnlyOnRequest()
    {
        // Arrange
        Queue(Item("новый.pdf", 1), Item("качается.zip", 0, ready: false), Item("потом.docx", 2, InboxItemState.Postponed), Item("оставить.exe", 3, InboxItemState.Kept));
        var page = await LoadAsync();

        // Act
        var shown = Names(page);
        page.ToggleShowKeptCommand.Execute(null);

        // Assert
        shown.ShouldBe(["новый.pdf", "потом.docx"]);
        Names(page).ShouldBe(["новый.pdf", "потом.docx", "оставить.exe"]);
        page.Items[1].IsPostponed.ShouldBeTrue();
        page.Items[1].StateText.ShouldBe("отложен");
        page.PendingCount.ShouldBe(1);
        page.PendingText.ShouldBe("Ещё загружается: 1 файл");
        page.BadgeValue.ShouldBe(1, "отложенные, оставленные и дописываемые не ждут разбора");
        page.Inboxes.Select(static inbox => inbox.Text).ShouldBe(["Все накопители · 1", "Загрузки · 1"]);
        page.KeptText.ShouldBe("Скрыть оставленные");
    }

    [Fact]
    public async Task Postpone_KeepAndReturn_SendSelectedPathsAndReload()
    {
        // Arrange
        Queue(Item("новый.pdf", 1), Item("потом.docx", 2, InboxItemState.Postponed));
        var page = await LoadAsync();

        // Act
        page.SetSelection([page.Items[0]]);
        var newOnly = page.SelectionHasState;
        await page.PostponeCommand.ExecuteAsync(null);
        page.SetSelection([page.Items[1]]);
        await page.ReturnToNewCommand.ExecuteAsync(null);
        page.SetSelection([.. page.Items]);
        await page.KeepHereCommand.ExecuteAsync(null);

        // Assert
        newOnly.ShouldBeFalse();
        await _inbox.Received(1).SetStateAsync(Arg.Is<IReadOnlyList<string>>(paths => paths.Single() == Path.Combine(Downloads, "новый.pdf")), InboxItemState.Postponed, Arg.Any<CancellationToken>());
        await _inbox.Received(1).SetStateAsync(Arg.Is<IReadOnlyList<string>>(paths => paths.Single() == Path.Combine(Downloads, "потом.docx")), InboxItemState.New, Arg.Any<CancellationToken>());
        await _inbox.Received(1).SetStateAsync(Arg.Is<IReadOnlyList<string>>(paths => paths.Count == 2), InboxItemState.Kept, Arg.Any<CancellationToken>());
        await _inbox.Received(4).GetAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Changed_NewDownload_ReloadsAndKeepsSelection()
    {
        // Arrange
        var first = Item("договор.docx", 2);
        Queue(first);
        var page = await LoadAsync();
        var row = page.Items[0];
        page.SetSelection([row]);
        Queue(Item("счёт.pdf", 1), first);

        // Act: пришёл файл — очередь перечитывается сама через короткую паузу
        _inbox.Changed += Raise.Event();
        await WaitForAsync(() => page.Items.Count == 2);
        await page.PendingLoad;

        // Assert
        Names(page).ShouldBe(["счёт.pdf", "договор.docx"]);
        page.Items[1].ShouldBeSameAs(row, "та же строка — выделение в списке не пропадает");
        page.SelectedCount.ShouldBe(1);
        page.BadgeValue.ShouldBe(2);
    }

    [Fact]
    public async Task ToggleSubfolders_SavesFlagOnInbox()
    {
        // Arrange
        Queue();
        var page = await LoadAsync();

        // Act
        await page.ToggleSubfoldersCommand.ExecuteAsync(page.Inboxes[1]);

        // Assert
        (await _settings.GetAsync(TestContext.Current.CancellationToken)).Inboxes.ShouldHaveSingleItem().IncludeSubfolders.ShouldBeTrue();
        page.Inboxes[1].Folder!.IncludeSubfolders.ShouldBeTrue();
    }

    [Fact]
    public async Task Problem_FromQueue_ShownOnChipAndHint()
    {
        // Arrange
        _inbox.GetAsync(Arg.Any<CancellationToken>()).Returns(new InboxSnapshotInfo([new InboxFolderState(Downloads, "диск недоступен")], [], null));

        // Act
        var page = await LoadAsync();

        // Assert
        page.Inboxes[1].Text.ShouldBe("Загрузки · !");
        page.SelectedInbox = page.Inboxes[1];
        page.EmptyHint.ShouldBe("Папка не прочитана: диск недоступен");
    }

    private static InboxEntry Item(string name, int hoursAgo, InboxItemState state = InboxItemState.New, bool ready = true)
        => new(new FileEntry { FullPath = Path.Combine(Downloads, name), Kind = FileKind.Document, Root = "user", Size = 100, Modified = Now.AddHours(-hoursAgo) }, Downloads, state, ready);

    private static List<string> Names(SortingViewModel page) => [.. page.Items.Select(static item => item.Entry.Name)];

    private static async Task WaitForAsync(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!done() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    private void Queue(params InboxEntry[] items)
        => _inbox.GetAsync(Arg.Any<CancellationToken>()).Returns(new InboxSnapshotInfo([new InboxFolderState(Downloads, null)], items, items.Any(static item => !item.IsReady) ? Now.AddMinutes(30) : null));

    private async Task<SortingViewModel> LoadAsync()
    {
        var page = new SortingViewModel(
            _settings,
            Substitute.For<IBrowseService>(),
            new SampleOperationsService(),
            Substitute.For<ISearchService>(),
            _library,
            Substitute.For<IRootsService>(),
            Substitute.For<IDialogService>(),
            Substitute.For<IInfoBarService>(),
            static () => Now,
            _inbox);
        await page.RefreshAsync();
        return page;
    }
}

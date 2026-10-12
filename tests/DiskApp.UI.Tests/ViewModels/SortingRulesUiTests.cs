using System.Text.Json.Nodes;

using DiskApp.Client;
using DiskApp.Contracts.App;
using DiskApp.Contracts.Entries;
using DiskApp.Contracts.Operations;
using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

using FluentAvalonia.UI.Controls;

using FileKind = DiskApp.UI.Models.FileKind;
using InboxItemState = DiskApp.UI.Services.InboxItemState;
using SortingRuleMode = DiskApp.Contracts.App.SortingRuleMode;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Шаблоны имён и авторазбор в интерфейсе (PLAN.md, этап 17.5): подсказки шаблонов в строках «Разбора», назначение по
/// подсказке (и в подпапку), выбор пользователя подсказка не перебивает; «Всегда класть такие файлы сюда?» — маска по
/// выбранным, сухой прогон, шаблон у рабочей папки, подпапки или новой рабочей папки; уведомление об авторазборе с
/// «Отменить» для всех переносов; шаблоны в карточке рабочей папки; шаблоны не теряются при сохранении настроек.
/// </summary>
public sealed class SortingRulesUiTests
{
    private const string Downloads = @"C:\Users\user\Downloads";
    private const string Work417 = @"D:\Work\417";
    private const string Tci2 = @"D:\Work\TCI2";

    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.FromHours(3));

    private readonly SampleSortingSettings _settings = new(new SortingFolders(
        [new SortingFolderInfo(Downloads, "Загрузки")],
        [new SortingFolderInfo(Work417) { Rules = [new SortingRuleInfo("417_*")] }, new SortingFolderInfo(Tci2, "Тест")]));

    private readonly IInboxService _inbox = Substitute.For<IInboxService>();
    private readonly IBrowseService _browse = Substitute.For<IBrowseService>();
    private readonly ILibraryService _library = Substitute.For<ILibraryService>();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly IInfoBarService _notices = Substitute.For<IInfoBarService>();
    private readonly IOperationsService _operations = Substitute.For<IOperationsService>();

    public SortingRulesUiTests()
    {
        _library.GetTagsAsync(Arg.Any<CancellationToken>()).Returns([]);
        _library.GetAutoTagsAsync(Arg.Any<CancellationToken>()).Returns([]);
        _library.ResolveAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(new ResolveResult([], []));
        _browse.ListAsync(Work417, Arg.Any<BrowseSort>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new FolderListing(Work417, 1, null, [Folder(Work417, "Акты"), Folder(Work417, "Переписка")], false));
        _browse.ListAsync(Arg.Is<string>(path => path != Work417), Arg.Any<BrowseSort>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call => new FolderListing(call.ArgAt<string>(0), 1, null, [], false));
    }

    [Fact]
    public async Task Hints_AutoSuggestAndChoice_DescribeDestinationAndReason()
    {
        // Arrange
        Queue(
            Item("417_ТЗ.pdf", 1, SortingHint.Auto, Match(Work417, Work417, "417_*", auto: true)),
            Item("акт 07.pdf", 2, SortingHint.Suggest, Match(Work417, Work417 + @"\Акты", "акт*")),
            Item("417_TCI2.pdf", 3, SortingHint.Choice, Match(Work417, Work417, "417_*", auto: true), Match(Tci2, Tci2, "TCI2")),
            Item("фото.jpg", 4));

        // Act
        var page = await LoadAsync();

        // Assert
        page.Items.Select(static item => item.HintText).ShouldBe(["→ 417 — уйдёт сам", "→ 417 › Акты · шаблон акт*", "Подходит к 417, Тест — выберите", string.Empty]);
        page.Items[3].HasHint.ShouldBeFalse();
    }

    [Fact]
    public async Task Selection_WithSuggestion_ChoosesDestinationDownToSubfolder()
    {
        // Arrange
        Queue(Item("417_ТЗ.pdf", 1, SortingHint.Auto, Match(Work417, Work417, "417_*", auto: true)), Item("акт 07.pdf", 2, SortingHint.Suggest, Match(Work417, Work417 + @"\Акты", "акт*")));
        var page = await LoadAsync();

        // Act
        page.SetSelection([page.Items[0]]);
        await page.PendingSuggestion;
        var first = page.SelectedFolder?.Path;
        page.SetSelection([page.Items[1]]);
        await page.PendingSuggestion;

        // Assert: подсказка меняет и назначение, выбранное прошлой подсказкой
        first.ShouldBe(Work417);
        page.SelectedWorkFolder!.Folder.Path.ShouldBe(Work417);
        page.SelectedFolder!.Path.ShouldBe(Work417 + @"\Акты");
        page.DestinationText.ShouldBe("417 › Акты");
    }

    [Fact]
    public async Task Selection_AfterUserChoseDestination_SuggestionDoesNotOverride()
    {
        // Arrange
        Queue(Item("417_ТЗ.pdf", 1, SortingHint.Suggest, Match(Work417, Work417, "417_*")));
        var page = await LoadAsync();
        page.SelectedWorkFolder = page.WorkFolders.Single(static item => item.Folder.Path == Tci2);

        // Act
        page.SetSelection([page.Items[0]]);
        await page.PendingSuggestion;

        // Assert
        page.SelectedFolder!.Path.ShouldBe(Tci2);
    }

    [Fact]
    public async Task Selection_Choice_LeavesDestinationEmpty()
    {
        // Arrange
        Queue(Item("417_TCI2.pdf", 1, SortingHint.Choice, Match(Work417, Work417, "417_*", auto: true), Match(Tci2, Tci2, "TCI2")));
        var page = await LoadAsync();

        // Act
        page.SetSelection([page.Items[0]]);
        await page.PendingSuggestion;

        // Assert
        page.SelectedFolder.ShouldBeNull();
    }

    [Fact]
    public async Task AlwaysHere_IntoSubfolder_AddsRuleWithSubfolderAfterPreview()
    {
        // Arrange: два файла «417_…» выбраны, назначение — «417 › Переписка»
        Queue(Item("417_ТЗ.pdf", 1), Item("417_акт.docx", 2));
        _inbox.PreviewRulesAsync(Arg.Any<IReadOnlyList<SortingFolderInfo>>(), Arg.Any<CancellationToken>()).Returns(
        [
            new SortingPreviewInfo(Downloads + @"\417_ТЗ.pdf", "417_ТЗ.pdf", SortingHint.Auto, true, [Match(Work417, Work417 + @"\Переписка", "417_*", auto: true)], null),
            new SortingPreviewInfo(Downloads + @"\417_акт.docx", "417_акт.docx", SortingHint.Choice, false, [Match(Work417, Work417 + @"\Переписка", "417_*", auto: true), Match(Work417, Work417, "417_*", auto: true)], null),
        ]);
        SortingRuleDialogViewModel? shown = null;
        _dialogs.ShowSortingRuleAsync(Arg.Do<SortingRuleDialogViewModel>(dialog => shown = dialog)).Returns(true);
        var page = await LoadAsync();
        await ChooseAsync(page, Work417, "Переписка");
        page.SetSelection([.. page.Items]);

        // Act
        page.AlwaysHereText.ShouldBe("Всегда класть такие в «417 › Переписка»…");
        await page.AlwaysHereCommand.ExecuteAsync(null);
        await shown!.PendingPreview;

        // Assert
        shown.Question.ShouldBe("Всегда класть такие файлы в «417 › Переписка»?");
        shown.Rule.Pattern.ShouldBe("417_*");
        shown.PreviewText.ShouldBe("Сейчас подходят: 2 файла — «417_ТЗ.pdf», «417_акт.docx». Сами перенесутся: 1. Подходят и к другой папке: 1 — их разберёте вы.");
        var rules = (await _settings.GetAsync(TestContext.Current.CancellationToken)).WorkFolders.Single(static folder => folder.Path == Work417).Rules;
        rules.Select(static rule => (rule.Pattern, rule.Subfolder)).ShouldBe([("417_*", (string?)null), ("417_*", "Переписка")]);
        _notices.Received().Show("Разбор", "Шаблон «417_*»: такие файлы сами уйдут в «417 › Переписка».", FAInfoBarSeverity.Success);
    }

    [Fact]
    public async Task AlwaysHere_OutsideWorkFolders_AddsWorkFolderWithRule()
    {
        // Arrange: назначение найдено поиском, рабочей папкой оно не было
        Queue(Item("Пароли Brave.csv", 1));
        _dialogs.ShowSortingRuleAsync(Arg.Any<SortingRuleDialogViewModel>()).Returns(true);
        var roots = Substitute.For<IRootsService>();
        roots.GetRootsAsync(Arg.Any<CancellationToken>()).Returns([new RootInfo { Id = 1, Label = "D", Path = @"D:\" }]);
        var page = await LoadAsync(roots);
        page.ChooseFoundFolderCommand.Execute(new SortingFolderInfo(@"D:\МОИ", "МОИ"));
        page.SetSelection([page.Items[0]]);

        // Act
        await page.AlwaysHereCommand.ExecuteAsync(null);

        // Assert
        var added = (await _settings.GetAsync(TestContext.Current.CancellationToken)).WorkFolders[^1];
        added.Path.ShouldBe(@"D:\МОИ");
        added.Rules.ShouldHaveSingleItem().ShouldBe(new SortingRuleInfo("Пароли *"));
    }

    [Fact]
    public async Task AlwaysHere_DialogCancelled_SavesNothing()
    {
        // Arrange
        Queue(Item("417_ТЗ.pdf", 1));
        _dialogs.ShowSortingRuleAsync(Arg.Any<SortingRuleDialogViewModel>()).Returns(false);
        var page = await LoadAsync();
        page.SelectedWorkFolder = page.WorkFolders[0];
        page.SetSelection([page.Items[0]]);

        // Act
        await page.AlwaysHereCommand.ExecuteAsync(null);

        // Assert
        _settings.SaveCount.ShouldBe(0);
    }

    [Fact]
    public async Task AutoSorted_SeveralOperations_OneNoticeUndoingAll()
    {
        // Arrange
        Queue();
        var page = await LoadAsync(noticeDelay: TimeSpan.FromMilliseconds(20));
        Action? undo = null;
        _notices.Show(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<FAInfoBarSeverity>(), Arg.Any<string>(), Arg.Do<Action>(action => undo = action));
        var first = AutoSorted(Work417, Downloads + @"\417_ТЗ.pdf");
        var second = AutoSorted(Tci2, Downloads + @"\TCI2 отчёт.pdf", Downloads + @"\TCI2 план.pdf");

        // Act: событие о том же переносе приходит дважды, ручной перенос не считается
        Publish(first);
        Publish(first);
        Publish(second);
        Publish(new OperationInfo { Id = Guid.NewGuid(), Kind = FileOperationKind.Move, State = FileOperationState.Completed, Sources = [Downloads + @"\вручную.pdf"], Destination = Work417 });
        await page.PendingAutoSortNotice;
        undo!.Invoke();

        // Assert
        _notices.Received(1).Show("Авторазбор", "3 файла — по папкам: 2", FAInfoBarSeverity.Success, "Отменить", Arg.Any<Action>());
        await _operations.Received().UndoAsync(first.Id, Arg.Any<CancellationToken>());
        await _operations.Received().UndoAsync(second.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AutoSorted_OneFile_NamesFileAndFolder()
    {
        // Arrange
        Queue();
        var page = await LoadAsync(noticeDelay: TimeSpan.FromMilliseconds(20));

        // Act
        Publish(AutoSorted(Work417 + @"\Акты", Downloads + @"\акт 07.pdf"));
        await page.PendingAutoSortNotice;

        // Assert
        _notices.Received(1).Show("Авторазбор", "«акт 07.pdf» → 417 › Акты", FAInfoBarSeverity.Success, "Отменить", Arg.Any<Action>());
    }

    [Fact]
    public void OperationsPage_AutoSortMove_IsLabelledAutoSorting()
        => OperationItemViewModel.Describe(AutoSorted(Work417, Downloads + @"\417_ТЗ.pdf")).ShouldBe("Авторазбор: «417_ТЗ.pdf» в «417»");

    [Fact]
    public async Task Card_Rules_EditedRemovedAndSavedWithPreview()
    {
        // Arrange
        _inbox.PreviewRulesAsync(Arg.Any<IReadOnlyList<SortingFolderInfo>>(), Arg.Any<CancellationToken>()).Returns(
            [new SortingPreviewInfo(Downloads + @"\акт 07.pdf", "акт 07.pdf", SortingHint.Suggest, false, [Match(Work417, Work417, "акт*")], null)]);
        WorkFolderCardViewModel? card = null;
        _dialogs.ShowWorkFolderCardAsync(Arg.Do<WorkFolderCardViewModel>(shown =>
        {
            card = shown;
            shown.Rules[0].Pattern = "417_*.pdf";
            shown.AddRuleCommand.Execute(null);
            shown.Rules[1].Pattern = "акт*";
            shown.Rules[1].SelectedMode = shown.Rules[1].Modes[1];
            shown.Rules[1].ExtensionsText = ".PDF; docx";
            shown.Rules[1].SelectedInbox = shown.Rules[1].InboxOptions[1];
            shown.AddRuleCommand.Execute(null);
        })).Returns(WorkFolderCardResult.Saved);
        Queue();
        var page = await LoadAsync();

        // Act
        await page.EditWorkFolderCommand.ExecuteAsync(page.WorkFolders[0]);
        await card!.PendingPreview;

        // Assert: пустой третий шаблон не сохраняется, у второго — условия
        card.PreviewText.ShouldBe("Сейчас подходят: 1 файл — «акт 07.pdf».");
        card.Rules[1].ConditionsText.ShouldBe("только из «Загрузки» · pdf, docx");
        var saved = (await _settings.GetAsync(TestContext.Current.CancellationToken)).WorkFolders[0].Rules;
        saved.Count.ShouldBe(2);
        saved[0].ShouldBe(new SortingRuleInfo("417_*.pdf"));
        saved[1].ShouldBe(new SortingRuleInfo("акт*", Auto: false, Inbox: Downloads) { Extensions = ["pdf", "docx"] });
    }

    [Theory]
    [InlineData("Работа/417_*", "")]
    [InlineData("417", "..\\Чужая")]
    public void Card_BadRule_CannotBeSaved(string pattern, string subfolder)
    {
        // Arrange
        var card = new WorkFolderCardViewModel(new SortingFolderInfo(Work417), 0, 1, isMissing: false);
        card.AddRuleCommand.Execute(null);

        // Act
        card.Rules[0].Pattern = pattern;
        card.Rules[0].Subfolder = subfolder;

        // Assert
        card.CanSave.ShouldBeFalse();
        card.Rules[0].HasProblem.ShouldBeTrue();
        card.Rules[0].RemoveCommand.Execute(null);
        card.CanSave.ShouldBeTrue();
        card.HasNoRules.ShouldBeTrue();
    }

    [Fact]
    public async Task ApiSettings_SaveAndRead_KeepRulesOfWorkFolders()
    {
        // Arrange
        var api = Substitute.For<IDiskAppApi>();
        JsonObject? patch = null;
        api.PatchSettingsAsync(Arg.Do<JsonObject>(sent => patch = sent), Arg.Any<CancellationToken>()).Returns(new SettingsDto());
        api.GetSettingsAsync(Arg.Any<CancellationToken>()).Returns(new SettingsDto
        {
            Sorting = new SortingSettingsDto
            {
                WorkFolders = [new SortingFolderDto { Path = Work417, Rules = [new SortingRuleDto { Pattern = "акт*", Mode = SortingRuleMode.Suggest, Enabled = false, Extensions = ["pdf"], Subfolder = "Акты" }] }],
            },
        });
        var settings = new ApiSortingSettings(api);
        var rule = new SortingRuleInfo("417_*", Inbox: Downloads) { Extensions = ["pdf"] };

        // Act
        await settings.SaveAsync(new SortingFolders([new SortingFolderInfo(Downloads)], [new SortingFolderInfo(Work417) { Rules = [rule] }]), TestContext.Current.CancellationToken);
        var read = await settings.GetAsync(TestContext.Current.CancellationToken);

        // Assert
        var sent = patch!["sorting"]!["workFolders"]![0]!["rules"]![0]!;
        sent["pattern"]!.GetValue<string>().ShouldBe("417_*");
        sent["mode"]!.GetValue<string>().ShouldBe("Auto");
        sent["inbox"]!.GetValue<string>().ShouldBe(Downloads);
        patch["sorting"]!["inboxes"]![0]!.AsObject().ContainsKey("rules").ShouldBeFalse();
        read.WorkFolders.ShouldHaveSingleItem().Rules.ShouldHaveSingleItem().ShouldBe(new SortingRuleInfo("акт*", Auto: false, Enabled: false, Subfolder: "Акты") { Extensions = ["pdf"] });
    }

    [Fact]
    public async Task ApiInbox_HintsAndPreview_AreMapped()
    {
        // Arrange
        var api = Substitute.For<IDiskAppApi>();
        var match = new SortingMatchDto(Work417, Work417, "417_*", SortingRuleMode.Auto);
        api.GetRootsAsync(Arg.Any<CancellationToken>()).Returns([]);
        api.GetInboxAsync(Arg.Any<CancellationToken>()).Returns(new InboxDto(
            [new InboxFolderDto(Downloads, null, InboxProblem.None)],
            [new InboxItemDto(new EntryDto { Id = 5, RootId = 1, Name = "417_ТЗ.pdf", FullPath = Downloads + @"\417_ТЗ.pdf", RelPath = "417_ТЗ.pdf" }, Downloads, InboxState.New, true, Now) { Decision = SortingDecision.Auto, Matches = [match] }],
            1,
            null,
            false));
        api.PreviewSortingRulesAsync(Arg.Any<SortingPreviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SortingPreviewDto([new SortingPreviewItemDto(Downloads + @"\417_ТЗ.pdf", "417_ТЗ.pdf", Downloads, SortingDecision.Auto, true, [match], "417_417_ТЗ.pdf")]));
        var inbox = new ApiInboxService(api);

        // Act
        var queue = await inbox.GetAsync(TestContext.Current.CancellationToken);
        var preview = await inbox.PreviewRulesAsync([new SortingFolderInfo(Work417) { Rules = [new SortingRuleInfo("417_*")] }], TestContext.Current.CancellationToken);

        // Assert
        var item = queue.Items.ShouldHaveSingleItem();
        item.Hint.ShouldBe(SortingHint.Auto);
        item.Matches.ShouldHaveSingleItem().ShouldBe(new SortingMatchInfo(Work417, Work417, "417_*", true));
        preview.ShouldHaveSingleItem().NewName.ShouldBe("417_417_ТЗ.pdf");
        await api.Received().PreviewSortingRulesAsync(
            Arg.Is<SortingPreviewRequest>(request => request.WorkFolders!.Single().Rules.Single().Pattern == "417_*"),
            Arg.Any<CancellationToken>());
    }

    private static InboxEntry Item(string name, int hoursAgo, SortingHint hint = SortingHint.None, params SortingMatchInfo[] matches)
        => new(new FileEntry { FullPath = Path.Combine(Downloads, name), Kind = FileKind.Document, Root = "user", Size = 10, Modified = Now.AddHours(-hoursAgo) }, Downloads, InboxItemState.New, true, hint)
        {
            Matches = matches,
        };

    private static SortingMatchInfo Match(string workFolder, string destination, string pattern, bool auto = false) => new(workFolder, destination, pattern, auto);

    private static FileEntry Folder(string parent, string name)
        => new() { FullPath = Path.Combine(parent, name), Kind = FileKind.Folder, Root = "Work", Modified = Now.AddDays(-1) };

    private static OperationInfo AutoSorted(string destination, params string[] sources)
        => new() { Id = Guid.NewGuid(), Kind = FileOperationKind.Move, State = FileOperationState.Completed, Sources = sources, Destination = destination, IsAutoSort = true };

    private static async Task ChooseAsync(SortingViewModel page, string workFolder, string subfolder)
    {
        page.SelectedWorkFolder = page.WorkFolders.Single(item => item.Folder.Path == workFolder);
        var root = page.Tree.Single();
        await root.EnsureLoadedAsync();
        page.SelectedFolder = root.Children.Single(child => child.Name == subfolder);
    }

    private void Publish(OperationInfo info) => _operations.Updated += Raise.Event<EventHandler<OperationInfo>>(_operations, info);

    private void Queue(params InboxEntry[] items)
        => _inbox.GetAsync(Arg.Any<CancellationToken>()).Returns(new InboxSnapshotInfo([new InboxFolderState(Downloads, null)], items, null));

    private async Task<SortingViewModel> LoadAsync(IRootsService? roots = null, TimeSpan? noticeDelay = null)
    {
        var page = new SortingViewModel(_settings, _browse, _operations, Substitute.For<ISearchService>(), _library, roots ?? Substitute.For<IRootsService>(), _dialogs, _notices, static () => Now, _inbox)
        {
            AutoSortNoticeDelay = noticeDelay ?? TimeSpan.FromSeconds(1.5),
        };
        await page.RefreshAsync();
        return page;
    }
}

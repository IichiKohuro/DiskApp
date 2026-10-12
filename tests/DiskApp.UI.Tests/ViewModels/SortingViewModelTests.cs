using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Страница «Разбор» (PLAN.md, этап 17): накопители с числом файлов и новыми сверху, рабочие папки на клавишах 1–9,
/// дерево подпапок с новой папкой, теги с подсказками, перенос с отменой, добавление папок в индекс.
/// </summary>
public sealed class SortingViewModelTests
{
    private const string Downloads = @"C:\Users\user\Downloads";
    private const string Skype = @"C:\Users\user\Skype";
    private const string Work417 = @"D:\Work\417";
    private const string Soft = @"D:\Soft";

    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.FromHours(3));

    private static readonly string[] CustomerUrgent = ["заказчик", "срочно"];

    private static readonly string[] PhotoOnly = ["фото"];

    private readonly SampleSortingSettings _settings = new(new SortingFolders(
        [new SortingFolderInfo(Downloads, "Загрузки"), new SortingFolderInfo(Skype, "Skype")],
        [new SortingFolderInfo(@"D:\Work\TCI2"), new SortingFolderInfo(Work417), new SortingFolderInfo(Soft)]));

    private readonly IBrowseService _browse = Substitute.For<IBrowseService>();
    private readonly SampleOperationsService _operations = new();
    private readonly ISearchService _search = Substitute.For<ISearchService>();
    private readonly ILibraryService _library = Substitute.For<ILibraryService>();
    private readonly IRootsService _roots = Substitute.For<IRootsService>();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly IInfoBarService _notices = Substitute.For<IInfoBarService>();

    public SortingViewModelTests()
    {
        Listing(Downloads, File(Downloads, "ТЗ 417.docx", hoursAgo: 1), File(Downloads, "setup.exe", hoursAgo: 30));
        Listing(Skype, File(Skype, "схема.pdf", hoursAgo: 3));
        Listing(Work417, Folder(Work417, "Документация"), Folder(Work417, "Переписка"), File(Work417, "readme.txt", hoursAgo: 100));
        _library.GetTagsAsync(Arg.Any<CancellationToken>()).Returns([new TagInfo(1, "заказчик", null), new TagInfo(2, "договор", null), new TagInfo(3, "фото", null)]);
        _roots.GetRootsAsync(Arg.Any<CancellationToken>()).Returns([
            new RootInfo { Id = 1, Label = "user", Path = @"C:\Users\user" },
            new RootInfo { Id = 2, Label = "Work", Path = @"D:\Work" },
        ]);
    }

    [Fact]
    public async Task Refresh_InboxesWithCounts_FilesNewestFirstAndBadge()
    {
        // Arrange
        var page = Create();

        // Act
        await page.RefreshAsync();

        // Assert
        page.Inboxes.Select(static inbox => inbox.Text).ShouldBe(["Все накопители · 3", "Загрузки · 2", "Skype · 1"]);
        page.Items.Select(static item => item.Entry.Name).ShouldBe(["ТЗ 417.docx", "схема.pdf", "setup.exe"]);
        page.BadgeValue.ShouldBe(3);
        page.WorkFolders.Select(static item => item.Text).ShouldBe(["1 TCI2", "2 417", "3 Soft"]);
        page.HasEmptyHint.ShouldBeFalse();
    }

    [Fact]
    public Task Refresh_Overlapping_DoesNotDuplicateFiles() => HeadlessTest.RunAsync(async () =>
    {
        // Arrange: при запуске сразу на «Разборе» бейдж и вход на страницу обновляют её одновременно — оба ждут листинг.
        // На UI-потоке, как в приложении: без него продолжения двух обновлений шли бы параллельно на пуле потоков
        var gate = new TaskCompletionSource();
        var downloads = new FolderListing(Downloads, 1, null, [File(Downloads, "ТЗ 417.docx", hoursAgo: 1), File(Downloads, "setup.exe", hoursAgo: 30)], false);
        _browse.ListAsync(Downloads, Arg.Any<BrowseSort>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await gate.Task;
            return downloads;
        });
        var page = Create();

        // Act
        var first = page.RefreshAsync();
        var second = page.RefreshAsync();
        gate.SetResult();
        await Task.WhenAll(first, second);

        // Assert
        page.Items.Select(static item => item.Entry.Name).ShouldBe(["ТЗ 417.docx", "схема.pdf", "setup.exe"]);
        page.Inboxes.Count.ShouldBe(3);
        page.BadgeValue.ShouldBe(3);
    });

    [Fact]
    public async Task SelectInbox_ShowsOnlyItsFiles()
    {
        // Arrange
        var page = await CreateLoadedAsync();

        // Act
        page.SelectedInbox = page.Inboxes.Single(static inbox => inbox.Title == "Skype");

        // Assert
        page.Items.ShouldHaveSingleItem().Entry.Name.ShouldBe("схема.pdf");
    }

    [Fact]
    public async Task SelectWorkFolderByNumber_ShowsTreeWithSubfolders()
    {
        // Arrange
        var page = await CreateLoadedAsync();

        // Act
        page.SelectWorkFolderByNumberCommand.Execute(2);
        await page.Tree[0].Loading;

        // Assert
        page.SelectedWorkFolder!.Folder.Path.ShouldBe(Work417);
        page.SelectedFolder!.Path.ShouldBe(Work417);
        page.Tree[0].Children.Select(static node => node.Name).ShouldBe(["Документация", "Переписка"], "файлы в дерево не попадают");
        page.DestinationText.ShouldBe("417");
    }

    [Fact]
    public async Task Move_IntoNewSubfolderWithTags_SendsOperationAndRemovesFiles()
    {
        // Arrange
        var page = await CreateLoadedAsync();
        page.SetSelection([.. page.Items.Take(2)]);
        page.SelectWorkFolderByNumberCommand.Execute(2);
        await page.Tree[0].Loading;
        page.StartNewFolderCommand.Execute(null);
        page.NewFolderName = "Заказ 5";
        page.ConfirmNewFolderCommand.Execute(null);
        page.AddTagCommand.Execute("заказчик");
        page.TagText = "срочно";

        // Act
        await page.MoveCommand.ExecuteAsync(null);

        // Assert
        var operation = _operations.Sorted.ShouldHaveSingleItem();
        operation.Sources.ShouldBe([Path.Combine(Downloads, "ТЗ 417.docx"), Path.Combine(Skype, "схема.pdf")]);
        operation.Destination.ShouldBe(Work417);
        operation.NewFolder.ShouldBe("Заказ 5");
        operation.Tags.ShouldBe(["заказчик", "срочно"], "недописанный тег в поле тоже уходит");
        page.Items.ShouldHaveSingleItem().Entry.Name.ShouldBe("setup.exe");
        page.BadgeValue.ShouldBe(1);
        page.Tags.ShouldBeEmpty();
        _notices.Received(1).Show("Разбор", "2 файла → 417 › Заказ 5, теги: заказчик, срочно", FAInfoBarSeverity.Success, "Отменить", Arg.Any<Action>());
    }

    [Fact]
    public async Task Move_WithoutSelectionOrDestination_IsDisabled()
    {
        // Arrange
        var page = await CreateLoadedAsync();

        // Act
        var withoutAnything = page.MoveCommand.CanExecute(null);
        page.SetSelection([page.Items[0]]);
        var withoutDestination = page.MoveCommand.CanExecute(null);
        page.SelectWorkFolderByNumberCommand.Execute(3);
        var ready = page.MoveCommand.CanExecute(null);

        // Assert
        withoutAnything.ShouldBeFalse();
        withoutDestination.ShouldBeFalse();
        ready.ShouldBeTrue();
        page.MoveText.ShouldBe("Перенести 1 файл");
    }

    [Fact]
    public async Task NewFolder_InvalidName_IsRejected()
    {
        // Arrange
        var page = await CreateLoadedAsync();
        page.SelectWorkFolderByNumberCommand.Execute(2);

        // Act
        page.StartNewFolderCommand.Execute(null);
        page.NewFolderName = "bad|name";
        page.ConfirmNewFolderCommand.Execute(null);

        // Assert
        page.SelectedFolder!.Path.ShouldBe(Work417);
        _notices.Received(1).Show("Новая папка", "Недопустимое имя папки.", FAInfoBarSeverity.Warning);
    }

    [Fact]
    public async Task TagText_SuggestsKnownTagsNotYetAdded()
    {
        // Arrange
        var page = await CreateLoadedAsync();
        page.AddTagCommand.Execute("заказчик");

        // Act
        page.TagText = "до";

        // Assert
        page.TagSuggestions.ShouldBe(["договор"]);
    }

    [Fact]
    public async Task AddWorkFolder_OutsideIndex_AddsRootAndPinsWithNextNumber()
    {
        // Arrange
        var page = await CreateLoadedAsync();
        _dialogs.PickFolderAsync(Arg.Any<string>()).Returns(@"E:\Projects\TCI3");

        // Act
        await page.AddWorkFolderCommand.ExecuteAsync(null);

        // Assert
        await _roots.Received(1).AddAsync(@"E:\Projects\TCI3", Arg.Any<CancellationToken>());
        page.WorkFolders.Select(static item => item.Text).ShouldBe(["1 TCI2", "2 417", "3 Soft", "4 TCI3"]);
        page.SelectedWorkFolder!.Number.ShouldBe(4);
        _settings.SaveCount.ShouldBe(1);
    }

    [Fact]
    public async Task AddInbox_InsideIndex_DoesNotAddRoot()
    {
        // Arrange
        var page = await CreateLoadedAsync();
        _dialogs.PickFolderAsync(Arg.Any<string>()).Returns(@"C:\Users\user\Telegram");
        Listing(@"C:\Users\user\Telegram");

        // Act
        await page.AddInboxCommand.ExecuteAsync(null);

        // Assert
        await _roots.DidNotReceive().AddAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        page.Inboxes.Select(static inbox => inbox.Title).ShouldBe(["Все накопители", "Загрузки", "Skype", "Telegram"]);
    }

    [Fact]
    public async Task MoveWorkFolderDown_SwapsNumbers()
    {
        // Arrange
        var page = await CreateLoadedAsync();

        // Act
        await page.MoveWorkFolderDownCommand.ExecuteAsync(page.WorkFolders[0]);

        // Assert
        page.WorkFolders.Select(static item => item.Text).ShouldBe(["1 417", "2 TCI2", "3 Soft"]);
    }

    [Fact]
    public async Task MissingWorkFolder_IsMarkedFromIndex()
    {
        // Arrange: папки 417 нет в индексе — её переименовали или перенесли (этап 17.2)
        _library.ResolveAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(new ResolveResult([], [Work417]));

        // Act
        var page = await CreateLoadedAsync();

        // Assert
        var missing = page.WorkFolders.Single(static item => item.Folder.Path == Work417);
        missing.IsMissing.ShouldBeTrue();
        missing.Tip.ShouldContain("не найдена");
        page.WorkFolders.Where(static item => item.Folder.Path != Work417).ShouldAllBe(static item => !item.IsMissing);
    }

    [Fact]
    public async Task EditWorkFolder_SavesLabelAndNumber()
    {
        // Arrange
        var page = await CreateLoadedAsync();
        _dialogs.ShowWorkFolderCardAsync(Arg.Any<WorkFolderCardViewModel>()).Returns(static call =>
        {
            var card = call.Arg<WorkFolderCardViewModel>();
            card.Label = "  Проект 417 ";
            card.SelectedPosition = card.Positions[0];
            return WorkFolderCardResult.Saved;
        });

        // Act
        await page.EditWorkFolderCommand.ExecuteAsync(page.WorkFolders[1]);

        // Assert
        page.WorkFolders.Select(static item => item.Text).ShouldBe(["1 Проект 417", "2 TCI2", "3 Soft"]);
        var saved = (await _settings.GetAsync(TestContext.Current.CancellationToken)).WorkFolders[0];
        saved.ShouldBe(new SortingFolderInfo(Work417, "Проект 417"));
    }

    [Fact]
    public async Task EditWorkFolder_MissingFolderPickedAgain_SavesNewPath()
    {
        // Arrange: папку перенесли, в карточке выбрали, где она теперь
        _library.ResolveAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(new ResolveResult([], [Work417]));
        _dialogs.PickFolderAsync(Arg.Any<string>()).Returns(@"D:\Work\Архив\417");
        var page = await CreateLoadedAsync();
        bool? wasMissing = null;
        _dialogs.ShowWorkFolderCardAsync(Arg.Any<WorkFolderCardViewModel>()).Returns(async call =>
        {
            var card = call.Arg<WorkFolderCardViewModel>();
            wasMissing = card.IsMissing;
            await card.PickFolderCommand.ExecuteAsync(null);
            return WorkFolderCardResult.Saved;
        });

        // Act
        await page.EditWorkFolderCommand.ExecuteAsync(page.WorkFolders[1]);

        // Assert
        wasMissing.ShouldBe(true);
        (await _settings.GetAsync(TestContext.Current.CancellationToken)).WorkFolders[1].Path.ShouldBe(@"D:\Work\Архив\417");
        await _roots.DidNotReceive().AddAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EditWorkFolder_Removed_RemovesFromList()
    {
        // Arrange
        var page = await CreateLoadedAsync();
        _dialogs.ShowWorkFolderCardAsync(Arg.Any<WorkFolderCardViewModel>()).Returns(WorkFolderCardResult.Removed);

        // Act
        await page.EditWorkFolderCommand.ExecuteAsync(page.WorkFolders[0]);

        // Assert
        page.WorkFolders.Select(static item => item.Text).ShouldBe(["1 417", "2 Soft"]);
    }

    [Fact]
    public async Task EditWorkFolder_AutoTags_SavedAndNewTagGetsPaletteColor()
    {
        // Arrange: у папки авто-тег «фото»; в карточке его убрали, добавили тег из библиотеки и новый
        AutoTags(new FolderTagsInfo(Work417, [new TagInfo(3, "фото", null)]));
        var page = await CreateLoadedAsync();
        IReadOnlyList<string>? initial = null;
        _dialogs.ShowWorkFolderCardAsync(Arg.Any<WorkFolderCardViewModel>()).Returns(call =>
        {
            var editor = call.Arg<WorkFolderCardViewModel>().AutoTags!;
            initial = editor.Names;
            editor.RemoveCommand.Execute(editor.Tags[0]);
            editor.AddCommand.Execute("ЗАКАЗЧИК");
            editor.Text = "#срочно";
            editor.AddCommand.Execute(null);
            return WorkFolderCardResult.Saved;
        });

        // Act
        await page.EditWorkFolderCommand.ExecuteAsync(page.WorkFolders[1]);

        // Assert
        initial.ShouldBe(["фото"]);
        await _library.Received(1).CreateTagAsync("срочно", Arg.Is<string?>(color => color != null), Arg.Any<CancellationToken>());
        await _library.Received(1).SetAutoTagsAsync(Work417, Arg.Is<IReadOnlyList<string>>(tags => tags.SequenceEqual(CustomerUrgent)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WorkFolderAutoTags_FromChipMenu_SavedForTheWorkFolderItself()
    {
        // Arrange: «Авто-теги…» в меню чипа — для самой рабочей папки, без карточки
        AutoTags();
        var page = await CreateLoadedAsync();
        string? title = null;
        _dialogs.ShowAutoTagsAsync(Arg.Any<string>(), Arg.Any<AutoTagsEditorViewModel>()).Returns(call =>
        {
            title = call.ArgAt<string>(0);
            call.ArgAt<AutoTagsEditorViewModel>(1).AddCommand.Execute("фото");
            return true;
        });

        // Act
        await page.EditWorkFolderAutoTagsCommand.ExecuteAsync(page.WorkFolders[1]);

        // Assert
        title.ShouldBe("417");
        await _library.Received(1).SetAutoTagsAsync(Work417, Arg.Is<IReadOnlyList<string>>(tags => tags.SequenceEqual(PhotoOnly)), Arg.Any<CancellationToken>());
        await _dialogs.DidNotReceive().ShowWorkFolderCardAsync(Arg.Any<WorkFolderCardViewModel>());
    }

    [Fact]
    public async Task EditWorkFolder_UnchangedAutoTags_AreNotSaved()
    {
        // Arrange
        AutoTags(new FolderTagsInfo(Work417, [new TagInfo(3, "фото", null)]));
        var page = await CreateLoadedAsync();
        _dialogs.ShowWorkFolderCardAsync(Arg.Any<WorkFolderCardViewModel>()).Returns(WorkFolderCardResult.Saved);

        // Act
        await page.EditWorkFolderCommand.ExecuteAsync(page.WorkFolders[1]);

        // Assert
        await _library.DidNotReceive().SetAutoTagsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EditWorkFolder_PickedAgain_AutoTagsMoveToNewPath()
    {
        // Arrange
        AutoTags(new FolderTagsInfo(Work417, [new TagInfo(3, "фото", null)]));
        _dialogs.PickFolderAsync(Arg.Any<string>()).Returns(@"D:\Work\Архив\417");
        var page = await CreateLoadedAsync();
        _dialogs.ShowWorkFolderCardAsync(Arg.Any<WorkFolderCardViewModel>()).Returns(async call =>
        {
            await call.Arg<WorkFolderCardViewModel>().PickFolderCommand.ExecuteAsync(null);
            return WorkFolderCardResult.Saved;
        });

        // Act
        await page.EditWorkFolderCommand.ExecuteAsync(page.WorkFolders[1]);

        // Assert
        await _library.Received(1).SetAutoTagsAsync(Work417, Arg.Is<IReadOnlyList<string>>(tags => tags.Count == 0), Arg.Any<CancellationToken>());
        await _library.Received(1).SetAutoTagsAsync(@"D:\Work\Архив\417", Arg.Is<IReadOnlyList<string>>(tags => tags.SequenceEqual(PhotoOnly)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EditWorkFolder_AutoTagsUnreadable_CardHidesField()
    {
        // Arrange: без ответа сервера поле не показывается — сохранение карточки не сотрёт авто-теги
        _library.GetAutoTagsAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<FolderTagsInfo>>(_ => throw new HttpRequestException("нет связи"));
        var page = await CreateLoadedAsync();
        bool? hasAutoTags = null;
        _dialogs.ShowWorkFolderCardAsync(Arg.Any<WorkFolderCardViewModel>()).Returns(call =>
        {
            hasAutoTags = call.Arg<WorkFolderCardViewModel>().HasAutoTags;
            return WorkFolderCardResult.Saved;
        });

        // Act
        await page.EditWorkFolderCommand.ExecuteAsync(page.WorkFolders[1]);

        // Assert
        hasAutoTags.ShouldBe(false);
        await _library.DidNotReceive().SetAutoTagsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RemoveWorkFolder_WithAutoTags_AsksWhetherToRemoveThem(bool remove)
    {
        // Arrange
        AutoTags(new FolderTagsInfo(Work417, [new TagInfo(3, "фото", null), new TagInfo(2, "договор", null)]));
        var page = await CreateLoadedAsync();
        string? question = null;
        _dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>()).Returns(call =>
        {
            question = call.ArgAt<string>(1);
            return remove;
        });

        // Act
        await page.RemoveWorkFolderCommand.ExecuteAsync(page.WorkFolders[1]);

        // Assert: папка убрана из списка в любом случае
        page.WorkFolders.Select(static item => item.Text).ShouldBe(["1 TCI2", "2 Soft"]);
        question.ShouldNotBeNull().ShouldContain("фото, договор");
        await _library.Received(remove ? 1 : 0).SetAutoTagsAsync(Work417, Arg.Is<IReadOnlyList<string>>(tags => tags.Count == 0), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveWorkFolder_WithoutAutoTags_DoesNotAsk()
    {
        // Arrange
        var page = await CreateLoadedAsync();

        // Act
        await page.RemoveWorkFolderCommand.ExecuteAsync(page.WorkFolders[0]);

        // Assert
        await _dialogs.DidNotReceive().ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task Destination_ShowsAutoTagsFilesWillGet_NearestFolderFirst()
    {
        // Arrange
        AutoTags(
            new FolderTagsInfo(Work417, [new TagInfo(3, "фото", null)]),
            new FolderTagsInfo(Path.Combine(Work417, "Переписка"), [new TagInfo(2, "договор", null)]),
            new FolderTagsInfo(@"D:\Work\4170", [new TagInfo(1, "заказчик", null)]));
        var page = await CreateLoadedAsync();
        var beforeChoice = page.HasDestinationAutoTags;
        page.SelectWorkFolderByNumberCommand.Execute(2);
        await page.Tree[0].Loading;

        // Act
        var atFolder = page.DestinationAutoTagsText;
        page.SelectedFolder = page.Tree[0].Children.Single(static node => node.Name == "Переписка");

        // Assert: «4170» начинается с «417», но это другая папка
        beforeChoice.ShouldBeFalse();
        atFolder.ShouldBe("Получат авто-теги: фото");
        page.DestinationAutoTagsText.ShouldBe("Получат авто-теги: договор, фото");
        page.HasDestinationAutoTags.ShouldBeTrue();
    }

    [Fact]
    public void WorkFolderCard_PositionsTitleAndFolder()
    {
        // Arrange
        var card = new WorkFolderCardViewModel(new SortingFolderInfo(Work417), position: 1, count: 11, isMissing: false);

        // Act
        card.Label = "   ";

        // Assert
        card.Positions[0].Text.ShouldBe("1 — клавиша 1");
        card.Positions[10].Text.ShouldBe("11 — без клавиши");
        card.SelectedPosition.ShouldBe(card.Positions[1]);
        card.Title.ShouldBe("417");
        card.ToFolder().ShouldBe(new SortingFolderInfo(Work417));
    }

    [Fact]
    public async Task NoInboxes_ShowsHintToAddOne()
    {
        // Arrange
        await _settings.SaveAsync(SortingFolders.Empty, TestContext.Current.CancellationToken);
        var page = Create();

        // Act
        await page.RefreshAsync();

        // Assert
        page.HasNoInboxes.ShouldBeTrue();
        page.HasNoWorkFolders.ShouldBeTrue();
        page.EmptyHint.ShouldStartWith("Добавьте папку-накопитель");
    }

    [Fact]
    public async Task InboxOutsideIndex_MarkedWithProblemWhileOthersLoad()
    {
        // Arrange
        _browse.ListAsync(Skype, Arg.Any<BrowseSort>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns<FolderListing>(_ => throw new InvalidOperationException("Папки нет в индексе"));
        var page = Create();

        // Act
        await page.RefreshAsync();
        page.SelectedInbox = page.Inboxes.Single(static inbox => inbox.Title == "Skype");

        // Assert
        page.Inboxes[0].Count.ShouldBe(2);
        page.SelectedInbox.Text.ShouldBe("Skype · !");
        page.EmptyHint.ShouldBe("Папка не прочитана: Папки нет в индексе");
    }

    [Fact]
    public async Task NamePreset_DateFirst_RenamesOnMoveAndIsRemembered()
    {
        // Arrange
        var page = await CreateLoadedAsync();
        page.SetSelection([.. page.Items.Take(2)]);
        page.SelectWorkFolderByNumberCommand.Execute(2);

        // Act
        page.SelectedNamePreset = page.NamePresets.Single(static preset => preset.Title == "Дата в начале");
        var preview = page.NamePreview;
        await page.MoveCommand.ExecuteAsync(null);
        var reopened = Create();
        await reopened.RefreshAsync();

        // Assert
        page.SelectedNamePreset.ShouldBeNull("список возвращается к подсказке");
        preview.ShouldBe("Будет: 2026-10-11_ТЗ 417.docx и ещё 1");
        _operations.Sorted.ShouldHaveSingleItem().Names.ShouldBe(["2026-10-11_ТЗ 417.docx", "2026-10-11_схема.pdf"]);
        page.NameTemplate.ShouldBe("{дата}_{имя}");
        reopened.NameTemplate.ShouldBe("{дата}_{имя}", "шаблон с прежним именем запомнен в настройках");
    }

    [Fact]
    public async Task CustomName_OneFile_RenamesOnceThenResets()
    {
        // Arrange
        var page = await CreateLoadedAsync();
        page.SetSelection([page.Items[0]]);
        page.SelectWorkFolderByNumberCommand.Execute(2);
        page.NameTemplate = "Техзадание заказчика";

        // Act
        await page.MoveCommand.ExecuteAsync(null);

        // Assert
        _operations.Sorted.ShouldHaveSingleItem().Names.ShouldBe(["Техзадание заказчика.docx"]);
        page.NameTemplate.ShouldBeEmpty("своё имя без {имя} не запоминается для следующих файлов");
        _settings.SaveCount.ShouldBe(0);
        _notices.Received(1).Show("Разбор", "«ТЗ 417.docx» → 417 как «Техзадание заказчика.docx»", FAInfoBarSeverity.Success, "Отменить", Arg.Any<Action>());
    }

    [Fact]
    public async Task InsertNameToken_AppendsWithSeparator_AndTagsGoIntoName()
    {
        // Arrange
        var page = await CreateLoadedAsync();
        page.SetSelection([page.Items[0]]);
        page.SelectWorkFolderByNumberCommand.Execute(3);

        // Act
        page.InsertNameTokenCommand.Execute("name");
        page.InsertNameTokenCommand.Execute("tags");
        page.TagText = "заказчик";

        // Assert
        page.NameTemplate.ShouldBe("{имя}_{теги}");
        page.NamePreview.ShouldBe("Будет: ТЗ 417_заказчик.docx");
    }

    [Fact]
    public async Task EmptyTemplate_SendsNoNames()
    {
        // Arrange
        var page = await CreateLoadedAsync();
        page.SetSelection([page.Items[0]]);
        page.SelectWorkFolderByNumberCommand.Execute(3);

        // Act
        await page.MoveCommand.ExecuteAsync(null);

        // Assert
        _operations.Sorted.ShouldHaveSingleItem().Names.ShouldBeNull();
        page.HasNamePreview.ShouldBeFalse();
    }

    private static FileEntry File(string folder, string name, int hoursAgo)
        => new() { FullPath = Path.Combine(folder, name), Kind = FileKind.Document, Root = "user", Size = 1024, Modified = Now.AddHours(-hoursAgo) };

    private static FileEntry Folder(string folder, string name)
        => new() { FullPath = Path.Combine(folder, name), Kind = FileKind.Folder, Root = "Work", Modified = Now.AddDays(-10) };

    private void AutoTags(params FolderTagsInfo[] folders)
    {
        _library.GetAutoTagsAsync(Arg.Any<CancellationToken>()).Returns(folders);
        _library.SetAutoTagsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(static call => new FolderTagsInfo(call.ArgAt<string>(0), []));
    }

    private void Listing(string path, params FileEntry[] items)
        => _browse.ListAsync(path, Arg.Any<BrowseSort>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new FolderListing(path, 1, null, items, false));

    private SortingViewModel Create() => new(_settings, _browse, _operations, _search, _library, _roots, _dialogs, _notices, static () => Now);

    private async Task<SortingViewModel> CreateLoadedAsync()
    {
        var page = Create();
        await page.RefreshAsync();
        return page;
    }
}

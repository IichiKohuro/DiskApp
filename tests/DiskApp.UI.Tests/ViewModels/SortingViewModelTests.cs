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

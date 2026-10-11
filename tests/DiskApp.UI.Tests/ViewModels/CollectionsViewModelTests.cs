using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Страница «Коллекции» (PLAN.md, этап 8.7): избранное первым, состав ручной коллекции и сохранённого запроса,
/// добавление по путям, порядок, исключение, выгрузка и загрузка списка путей, автообновление сохранённого запроса.
/// </summary>
public sealed class CollectionsViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.FromHours(3));

    private readonly SampleLibraryService _library = new(static () => Now);
    private readonly SampleFileActions _actions = new();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly IInfoBarService _notices = Substitute.For<IInfoBarService>();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "DiskApp.CollectionsTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Refresh_FavoritesFirstThenCollections()
    {
        // Arrange
        var page = Create();

        // Act
        await page.RefreshAsync();

        // Assert
        page.Lists[0].Kind.ShouldBe(CollectionListKind.Favorites);
        page.Lists.Skip(1).Select(static item => item.Title).ShouldBe(["Все PDF", "Проект DiskApp"]);
        page.SelectedList.ShouldBe(page.Lists[0]);
        page.Files.ShouldAllBe(hit => hit.Entry.IsFavorite);
        page.IsEditable.ShouldBeTrue();
        page.IsCollection.ShouldBeFalse();
    }

    [Fact]
    public async Task SelectManual_LoadsItsItems()
    {
        // Arrange
        var page = await CreateLoadedAsync();

        // Act
        page.SelectedList = Manual(page);
        await page.PendingLoad;

        // Assert
        page.Files.Count.ShouldBe(3);
        page.Files.ShouldAllBe(hit => hit.Entry.FullPath.Contains("DiskApp"));
        page.IsCollection.ShouldBeTrue();
    }

    [Fact]
    public async Task SelectSavedQuery_ShowsSearchResultsAndIsReadOnly()
    {
        // Arrange
        var page = await CreateLoadedAsync();

        // Act
        page.SelectedList = page.Lists.Single(item => item.Kind == CollectionListKind.SavedQuery);
        await page.PendingLoad;

        // Assert
        page.Files.ShouldNotBeEmpty();
        page.Files.ShouldAllBe(hit => hit.Entry.Name.Contains("pdf", StringComparison.OrdinalIgnoreCase));
        page.IsEditable.ShouldBeFalse();
        page.ImportCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task AddPaths_ToManual_AddsKnownAndReportsMissing()
    {
        // Arrange
        var page = await CreateLoadedAsync();
        page.SelectedList = Manual(page);
        await page.PendingLoad;
        var extra = (await _library.GetFavoritesAsync(TestContext.Current.CancellationToken))
            .First(entry => page.Files.All(hit => hit.Entry.Id != entry.Id));

        // Act
        var added = await page.AddPathsAsync([extra.FullPath, @"Z:\нет такого.txt"]);

        // Assert
        added.ShouldBe(1);
        page.Files.Count.ShouldBe(4);
        page.Files[^1].Entry.Id.ShouldBe(extra.Id);
        _notices.Received(1).Show("«Проект DiskApp»", "1 запись добавлена; 1 путь вне индекса", FAInfoBarSeverity.Warning);
    }

    [Fact]
    public async Task AddPaths_ToFavorites_PinsThroughFileActions()
    {
        // Arrange
        var page = await CreateLoadedAsync();
        var entry = (await _library.GetCollectionItemsAsync(new CollectionInfo(1, "Проект DiskApp", CollectionType.Manual, null), TestContext.Current.CancellationToken))
            .First(static entry => !entry.IsFavorite);

        // Act
        var added = await page.AddPathsAsync([entry.FullPath], page.Lists[0]);

        // Assert
        added.ShouldBe(1);
        _actions.Calls.ShouldContain(call => call == "Pin: " + entry.FullPath);
    }

    [Fact]
    public async Task MoveDown_Manual_PersistsOrder()
    {
        // Arrange
        var page = await CreateLoadedAsync();
        page.SelectedList = Manual(page);
        await page.PendingLoad;
        var first = page.Files[0].Entry.Id;
        page.SelectedFile = page.Files[0];

        // Act
        await page.MoveDownCommand.ExecuteAsync(null);
        page.SelectedList = page.Lists[0];
        await page.PendingLoad;
        page.SelectedList = Manual(page);
        await page.PendingLoad;

        // Assert
        page.Files[1].Entry.Id.ShouldBe(first);
    }

    [Fact]
    public async Task RemoveFile_Manual_ExcludesWithoutTouchingFile()
    {
        // Arrange
        var page = await CreateLoadedAsync();
        page.SelectedList = Manual(page);
        await page.PendingLoad;
        var removed = page.Files[1].Entry.Id;
        page.SelectedFile = page.Files[1];

        // Act
        await page.RemoveFileCommand.ExecuteAsync(null);

        // Assert
        page.Files.Count.ShouldBe(2);
        page.Files.ShouldNotContain(hit => hit.Entry.Id == removed);
        _actions.Calls.ShouldBeEmpty("файл не удаляется и не открепляется");
    }

    [Fact]
    public async Task ExportThenImport_RoundTripsPathsIntoAnotherCollection()
    {
        // Arrange
        Directory.CreateDirectory(_folder);
        var file = Path.Combine(_folder, "list.txt");
        var page = await CreateLoadedAsync();
        page.SelectedList = Manual(page);
        await page.PendingLoad;
        var expected = page.Files.Select(static hit => hit.Entry.Id).ToArray();
        _dialogs.PickSaveFileAsync(Arg.Any<string>(), "Проект DiskApp.txt").Returns(file);
        _dialogs.PickOpenFileAsync(Arg.Any<string>()).Returns(file);
        _dialogs.PromptAsync("Новая коллекция", "Имя", string.Empty, "Создать").Returns("Копия");

        // Act
        await page.ExportCommand.ExecuteAsync(null);
        await page.NewCollectionCommand.ExecuteAsync(null);
        await page.ImportCommand.ExecuteAsync(null);

        // Assert
        page.SelectedList!.Title.ShouldBe("Копия");
        page.Files.Select(static hit => hit.Entry.Id).ShouldBe(expected);
        (await File.ReadAllLinesAsync(file, TestContext.Current.CancellationToken)).Length.ShouldBe(3);
    }

    [Fact]
    public async Task Delete_Confirmed_RemovesCollectionAndSelectsFavorites()
    {
        // Arrange
        var page = await CreateLoadedAsync();
        page.SelectedList = Manual(page);
        await page.PendingLoad;
        _dialogs.ConfirmAsync("Удалить коллекцию", Arg.Any<string>(), "Удалить").Returns(true);

        // Act
        await page.DeleteCommand.ExecuteAsync(null);

        // Assert
        page.Lists.ShouldNotContain(item => item.Title == "Проект DiskApp");
        page.SelectedList!.Kind.ShouldBe(CollectionListKind.Favorites);
    }

    [Fact]
    public async Task NewSavedQuery_AsksQueryThenName()
    {
        // Arrange
        var page = await CreateLoadedAsync();
        _dialogs.PromptAsync("Новый сохранённый запрос", Arg.Is<string>(label => label.StartsWith("Запрос", StringComparison.Ordinal)), string.Empty, "Далее").Returns("readme");
        _dialogs.PromptAsync("Новый сохранённый запрос", "Имя", "readme", "Создать").Returns("Ридми");

        // Act
        await page.NewSavedQueryCommand.ExecuteAsync(null);

        // Assert
        page.SelectedList!.Kind.ShouldBe(CollectionListKind.SavedQuery);
        page.SelectedList.Subtitle.ShouldBe("readme");
        page.Files.ShouldAllBe(hit => hit.Entry.Name.Contains("readme", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SavedQuery_WhileVisible_RefreshesByTimerAndStopsWhenLeft()
    {
        // Arrange
        var library = Substitute.For<ILibraryService>();
        var query = new CollectionInfo(5, "Свежее", CollectionType.SavedQuery, "x");
        library.GetCollectionsAsync(Arg.Any<CancellationToken>()).Returns([query]);
        library.GetFavoritesAsync(Arg.Any<CancellationToken>()).Returns([]);
        library.GetCollectionItemsAsync(query, Arg.Any<CancellationToken>()).Returns([]);
        using var page = new CollectionsViewModel(library, new SampleSearchService(), _actions, _dialogs, _notices, null, static () => Now)
        {
            AutoRefreshInterval = TimeSpan.FromMilliseconds(40),
        };
        await page.RefreshAsync();
        page.SelectedList = page.Lists[1];
        await page.PendingLoad;

        // Act
        page.OnNavigatedTo();
        await Task.Delay(400, TestContext.Current.CancellationToken);
        page.OnNavigatedFrom();
        var whileVisible = library.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(ILibraryService.GetCollectionItemsAsync));
        await Task.Delay(200, TestContext.Current.CancellationToken);
        var afterLeaving = library.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(ILibraryService.GetCollectionItemsAsync));

        // Assert
        whileVisible.ShouldBeGreaterThanOrEqualTo(4, "загрузка при выборе и входе плюс несколько тиков таймера");
        afterLeaving.ShouldBe(whileVisible);
    }

    [Fact]
    public void ParsePaths_SkipsEmptyCommentsQuotesAndDuplicates()
    {
        // Act
        var paths = CollectionsViewModel.ParsePaths(["# список", "", "  \"D:\\a b.txt\" ", @"D:\c.txt", @"d:\C.TXT"]);

        // Assert
        paths.ShouldBe([@"D:\a b.txt", @"D:\c.txt"]);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        catch (IOException)
        {
            // Временная папка теста.
        }
    }

    [Fact]
    public async Task EmptyCollection_ShowsHintInsteadOfList()
    {
        // Arrange
        await _library.CreateCollectionAsync("Пустая", CollectionType.Manual, null, TestContext.Current.CancellationToken);
        var page = await CreateLoadedAsync();

        // Act
        page.SelectedList = page.Lists.Single(static item => item.Title == "Пустая");
        await page.PendingLoad;
        var emptyHint = page.EmptyHint;
        page.SelectedList = Manual(page);
        await page.PendingLoad;

        // Assert
        emptyHint.ShouldStartWith("Коллекция пуста");
        page.HasEmptyHint.ShouldBeFalse("в коллекции есть записи");
    }

    private static CollectionListItem Manual(CollectionsViewModel page)
        => page.Lists.Single(static item => item.Kind == CollectionListKind.Manual && item.Title == "Проект DiskApp");

    private CollectionsViewModel Create() => new(_library, new SampleSearchService(), _actions, _dialogs, _notices, null, static () => Now);

    private async Task<CollectionsViewModel> CreateLoadedAsync()
    {
        var page = Create();
        await page.RefreshAsync();
        return page;
    }
}

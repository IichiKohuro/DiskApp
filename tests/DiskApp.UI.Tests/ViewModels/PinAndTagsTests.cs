using Avalonia.Input;

using DiskApp.UI.Input;
using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Закрепление и теги из любого места (PLAN.md, этап 8.7): <c>Ctrl+D</c> и <c>Ctrl+T</c> в быстром поиске и «Обзоре»,
/// жизненный цикл страниц главного окна.
/// </summary>
public sealed class PinAndTagsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.FromHours(3));

    private readonly SampleFileActions _actions = new();
    private readonly ITagEditor _tags = Substitute.For<ITagEditor>();
    private readonly IInfoBarService _notices = Substitute.For<IInfoBarService>();

    [Theory]
    [InlineData(Key.D, QuickSearchCommand.TogglePin)]
    [InlineData(Key.T, QuickSearchCommand.EditTags)]
    public void KeyMap_CtrlLetter_MapsToLibraryCommands(Key key, QuickSearchCommand expected)
    {
        // Act
        var command = QuickSearchKeyMap.Resolve(key, KeyModifiers.Control, new QueryBoxState(IsEmpty: false, HasSelection: false, CaretAtEnd: true));

        // Assert
        command.ShouldNotBeNull().Command.ShouldBe(expected);
        QuickSearchKeyMap.TogglePinText.ShouldBe("Ctrl+D");
        QuickSearchKeyMap.EditTagsText.ShouldBe("Ctrl+T");
    }

    [Fact]
    public async Task QuickSearch_EditTags_ReplacesRowAndShowsSuccess()
    {
        // Arrange
        using var viewModel = new QuickSearchViewModel(new SampleSearchService(static () => Now), _actions, _tags) { DebounceDelay = TimeSpan.Zero };
        viewModel.Query = "бюджет";
        await viewModel.PendingRefresh;
        var entry = viewModel.SelectedResult!.Entry;
        _tags.EditAsync(Arg.Any<IReadOnlyList<FileEntry>>()).Returns([entry with { Tags = ["налоги"] }]);

        // Act
        await viewModel.ExecuteAsync(QuickSearchCommand.EditTags);

        // Assert
        viewModel.CanEditTags.ShouldBeTrue();
        viewModel.SelectedResult!.Entry.Tags.ShouldBe(["налоги"]);
        viewModel.NoticeTitle.ShouldBe("Теги сохранены");
        viewModel.NoticeSeverity.ShouldBe(FAInfoBarSeverity.Success);
        await _tags.Received(1).EditAsync(Arg.Is<IReadOnlyList<FileEntry>>(list => list.Single().FullPath == entry.FullPath));
    }

    [Fact]
    public async Task QuickSearch_EditTagsCancelled_KeepsRowWithoutNotice()
    {
        // Arrange
        using var viewModel = new QuickSearchViewModel(new SampleSearchService(static () => Now), _actions, _tags) { DebounceDelay = TimeSpan.Zero };
        viewModel.Query = "бюджет";
        await viewModel.PendingRefresh;
        var before = viewModel.SelectedResult!.Entry;
        _tags.EditAsync(Arg.Any<IReadOnlyList<FileEntry>>()).Returns((IReadOnlyList<FileEntry>?)null);

        // Act
        await viewModel.ExecuteAsync(QuickSearchCommand.EditTags);

        // Assert
        viewModel.SelectedResult!.Entry.ShouldBe(before);
        viewModel.IsNoticeOpen.ShouldBeFalse();
    }

    [Fact]
    public void QuickSearch_WithoutTagEditor_HidesTags()
    {
        // Act
        using var viewModel = new QuickSearchViewModel(new SampleSearchService(static () => Now), _actions);

        // Assert
        viewModel.CanEditTags.ShouldBeFalse();
    }

    [Fact]
    public async Task Browse_TogglePin_MixedSelection_PinsAllUnpinned()
    {
        // Arrange
        var browse = await CreateBrowseAsync(@"D:\Projects\DiskApp");
        var pinned = browse.Items.First(static hit => hit.Entry.IsFavorite);
        var plain = browse.Items.Where(static hit => !hit.Entry.IsFavorite).Take(2).ToArray();
        browse.UpdateSelection([pinned, .. plain]);

        // Act
        await browse.TogglePinCommand.ExecuteAsync(null);

        // Assert
        _actions.Calls.ShouldBe(plain.Select(static hit => "Pin: " + hit.Entry.FullPath).ToArray(), "уже закреплённое не трогается");
        _notices.Received(1).Show("Закреплено", "2 элемента", FAInfoBarSeverity.Success);
    }

    [Fact]
    public async Task Browse_TogglePin_AllPinned_UnpinsAll()
    {
        // Arrange
        var browse = await CreateBrowseAsync(@"D:\Projects\DiskApp");
        var pinned = browse.Items.Where(static hit => hit.Entry.IsFavorite).ToArray();
        browse.UpdateSelection(pinned);

        // Act
        await browse.TogglePinCommand.ExecuteAsync(null);

        // Assert
        _actions.Calls.ShouldBe(pinned.Select(static hit => "Unpin: " + hit.Entry.FullPath).ToArray());
    }

    [Fact]
    public async Task Browse_EditTags_PassesWholeSelection()
    {
        // Arrange
        var browse = await CreateBrowseAsync(@"D:\Projects\DiskApp");
        var selection = browse.Items.Take(2).ToArray();
        browse.UpdateSelection(selection);
        _tags.EditAsync(Arg.Any<IReadOnlyList<FileEntry>>()).Returns(call => ((IReadOnlyList<FileEntry>)call[0]).Select(static entry => entry with { Tags = ["x"] }).ToArray());

        // Act
        await browse.EditTagsCommand.ExecuteAsync(null);

        // Assert
        browse.CanEditTags.ShouldBeTrue();
        await _tags.Received(1).EditAsync(Arg.Is<IReadOnlyList<FileEntry>>(list => list.Count == 2));
        _notices.Received(1).Show("Теги сохранены", "2 файла", FAInfoBarSeverity.Success);
    }

    [Fact]
    public async Task MainWindow_SelectingLibraryPage_LoadsItAndStopsWhenLeft()
    {
        // Arrange
        var library = Substitute.For<ILibraryService>();
        library.GetCollectionsAsync(Arg.Any<CancellationToken>()).Returns([]);
        library.GetFavoritesAsync(Arg.Any<CancellationToken>()).Returns([]);
        library.GetTagsAsync(Arg.Any<CancellationToken>()).Returns([]);
        var search = new SampleSearchService(static () => Now);
        var dialogs = Substitute.For<IDialogService>();
        using var collections = new CollectionsViewModel(library, search, _actions, dialogs, _notices, null, static () => Now);
        var tagsPage = new TagsViewModel(library, search, _actions, dialogs, _notices, null, static () => Now);
        var hotkeys = new HotkeyStatus();
        var quick = new QuickSearchViewModel(search, _actions, static () => Now);
        var main = new MainViewModel(
            new HomeViewModel(search, new DesignData.NullAppCommands(), hotkeys),
            quick,
            new BrowseViewModel(new SampleBrowseService(static () => Now), new SampleRootsService(), search, _actions, null, null, static () => Now),
            new OperationsViewModel(new SampleOperationsService(), new ImmediateDispatcher()),
            collections,
            tagsPage,
            new SettingsViewModel(Substitute.For<IThemeService>(), search, hotkeys),
            search);

        // Act
        main.NavigateTo(PageKeys.Collections);
        await collections.PendingLoad;
        main.NavigateTo(PageKeys.Tags);
        await tagsPage.PendingLoad;

        // Assert
        main.Pages.Select(static page => page.Key).ShouldContain(PageKeys.Collections);
        main.Pages.Single(static page => page.Key == PageKeys.Collections).ShouldBeSameAs(collections);
        await library.Received(1).GetCollectionsAsync(Arg.Any<CancellationToken>());
        await library.Received(1).GetTagsAsync(Arg.Any<CancellationToken>());
    }

    private async Task<BrowseViewModel> CreateBrowseAsync(string folder)
    {
        var browse = new BrowseViewModel(
            new SampleBrowseService(static () => Now),
            new SampleRootsService(),
            new SampleSearchService(static () => Now),
            _actions,
            Substitute.For<IDialogService>(),
            _notices,
            static () => Now)
        {
            TagEditor = _tags,
        };
        await browse.Initialization;
        await browse.NavigateAsync(folder);
        return browse;
    }
}

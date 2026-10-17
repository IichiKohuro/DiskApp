using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Проекты в интерфейсе (PLAN.md, этап 24.3–24.4): страница «Проекты» — список по номеру, новый проект сохраняется
/// с номером, занятый номер объясняется, папка из рабочих папок подставляет номер, из проекта открываются папка
/// и заметки; привязка заметки к проекту и фильтр заметок по проекту; иконка проекта на чипе рабочей папки.
/// </summary>
public sealed class ProjectsViewModelTests
{
    private const string Work417 = @"D:\Work\10S-417";
    private const string Work2 = @"D:\Work\2";

    private static readonly DateTimeOffset Start = new(2026, 10, 17, 9, 0, 0, TimeSpan.FromHours(3));

    private readonly SampleProjectsService _projects = new(new ProjectInput("417", "Школа", null, Work417), new ProjectInput("10", "Склад", "#0078D4", null), new ProjectInput("2", string.Empty, null, Work2));
    private readonly SampleNotesService _notes;
    private readonly SampleSortingSettings _sorting = new(new SortingFolders([], [new SortingFolderInfo(Work417), new SortingFolderInfo(@"D:\Work\10S-417\Акты"), new SortingFolderInfo(@"D:\Soft")]));
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly IInfoBarService _notices = Substitute.For<IInfoBarService>();
    private readonly IAppCommands _commands = Substitute.For<IAppCommands>();
    private DateTimeOffset _now = Start;

    public ProjectsViewModelTests()
    {
        _notes = new SampleNotesService(Tick);
        _projects.NoteCounter = _notes.CountForProject;
    }

    [Fact]
    public async Task List_ByNumber_Search_CardShowsFolderAndNotes()
    {
        // Arrange
        await _notes.CreateAsync("Смета\nпо 417", [], projectId: 1, cancellationToken: TestContext.Current.CancellationToken);
        await _notes.CreateAsync("Чужая", [], cancellationToken: TestContext.Current.CancellationToken);
        var (page, _) = await OpenAsync();

        // Act
        var numbers = page.Projects.Select(static item => item.Number).ToList();
        page.SelectedProject = page.Projects.Single(static item => item.Number == "417");
        await WaitAsync(() => page.HasProjectNotes);
        var notes = page.ProjectNotes.Select(static note => note.Title).ToList();
        var details = page.SelectedProject.Details;
        page.SearchText = "скл";

        // Assert
        numbers.ShouldBe(["2", "10", "417"]);
        notes.ShouldBe(["Смета"]);
        details.ShouldBe("10S-417 · 1 заметка");
        page.Projects.ShouldHaveSingleItem().Number.ShouldBe("10");
        page.Projects[0].Details.ShouldBe(string.Empty);
        (await _projects.GetProjectsAsync(TestContext.Current.CancellationToken)).Single(static project => project.Number == "417").NoteCount.ShouldBe(1);
    }

    [Fact]
    public async Task NewProject_TakenNumberExplained_SavedWithNumber_FolderFillsEmptyNumber()
    {
        // Arrange
        var (page, _) = await OpenAsync();
        var focus = 0;
        page.NumberFocusRequested += (_, _) => focus++;

        // Act: новый проект; номер «417» занят — подсказка и не сохраняется; папку выбрали из рабочих — номер из её имени
        await page.NewProjectCommand.ExecuteAsync(null);
        page.Number = " 417 ";
        await page.FlushAsync();
        var takenError = page.NumberError;
        var afterTaken = (await _projects.GetProjectsAsync(TestContext.Current.CancellationToken)).Count;
        page.Number = string.Empty;
        page.ChooseFolderCommand.Execute(@"D:\Work\TCI2");
        page.ProjectTitle = "Завод";
        page.SelectedColor = page.Colors[3];
        await page.FlushAsync();

        // Assert
        focus.ShouldBe(1);
        takenError.ShouldBe("Номер 417 уже у проекта «417 · Школа».");
        afterTaken.ShouldBe(3);
        page.HasNumberError.ShouldBeFalse();
        var saved = (await _projects.GetProjectsAsync(TestContext.Current.CancellationToken)).Single(static project => project.Number == "TCI2");
        (saved.Title, saved.FolderPath, saved.Color).ShouldBe(("Завод", @"D:\Work\TCI2", page.Colors[3].Color));
        page.SelectedProject!.Project.Id.ShouldBe(saved.Id);
    }

    [Fact]
    public async Task EmptyNewProject_Left_Disappears_DeleteKeepsNotes()
    {
        // Arrange
        await _notes.CreateAsync("Протокол", [], projectId: 3, cancellationToken: TestContext.Current.CancellationToken);
        var (page, _) = await OpenAsync();
        _dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>()).Returns(true);

        // Act
        await page.NewProjectCommand.ExecuteAsync(null);
        page.SelectedProject = page.Projects.Single(static item => item.Number == "417");
        var withoutDraft = page.Projects.Count;
        await page.DeleteCommand.ExecuteAsync(null);

        // Assert
        withoutDraft.ShouldBe(3);
        page.Projects.Select(static item => item.Number).ShouldBe(["2", "10"]);
        await _dialogs.Received(1).ConfirmAsync("Удалить проект 417 · Школа?", Arg.Any<string>(), "Удалить проект", Arg.Any<string?>());
        (await _notes.GetNotesAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldHaveSingleItem().Title.ShouldBe("Протокол");
    }

    [Fact]
    public async Task OpenFolder_OnBrowse_NewNote_OpensNotesPageInProject_SavedWithProject()
    {
        // Arrange
        var (page, notesPage) = await OpenAsync();
        page.SelectedProject = page.Projects.Single(static item => item.Number == "417");

        // Act: папка — на «Обзоре»; «Новая заметка» — на странице «Заметки» сразу в проекте
        page.OpenFolderCommand.Execute(null);
        await page.NewNoteCommand.ExecuteAsync(null);
        notesPage.OnNavigatedTo();
        await notesPage.PendingLoad;
        var draftProject = notesPage.SelectedProject?.Project?.Number;
        var filter = notesPage.SelectedProjectFilter?.Project?.Number;
        notesPage.Body = "Замеры\nкухня 3,2 м";
        await notesPage.FlushAsync();

        // Assert
        _commands.Received(1).BrowseFolder(Work417);
        _commands.Received(1).NavigateTo(PageKeys.Notes);
        (draftProject, filter).ShouldBe(("417", "417"));
        var note = (await _notes.GetNotesAsync(projectId: 1, cancellationToken: TestContext.Current.CancellationToken)).ShouldHaveSingleItem();
        note.Title.ShouldBe("Замеры");
        notesPage.Notes.ShouldHaveSingleItem().Project!.Number.ShouldBe("417");
    }

    [Fact]
    public async Task Notes_ProjectPickedInNote_FilterByProject_OpenFromProjectClearsFilter()
    {
        // Arrange
        var other = await _notes.CreateAsync("Общая", [], cancellationToken: TestContext.Current.CancellationToken);
        var (page, notesPage) = await OpenAsync();
        notesPage.OnNavigatedTo();
        await notesPage.PendingLoad;

        // Act: заметку привязали к «10» — она в отборе «10»; из проекта «10» открыли заметку при отборе «2»
        notesPage.SelectedNote = notesPage.Notes.Single(item => item.Note.Id == other.Id);
        notesPage.SelectedProject = notesPage.ProjectChoices.Single(static choice => choice.Project?.Number == "10");
        await notesPage.FlushAsync();
        notesPage.SelectedProjectFilter = notesPage.ProjectFilters.Single(static choice => choice.Project?.Number == "10");
        await notesPage.PendingLoad;
        var inFilter = notesPage.Notes.Select(static item => item.Title).ToList();
        notesPage.SelectedProjectFilter = notesPage.ProjectFilters.Single(static choice => choice.Project?.Number == "2");
        await notesPage.PendingLoad;
        page.SelectedProject = page.Projects.Single(static item => item.Number == "10");
        await WaitAsync(() => page.HasProjectNotes);
        await page.OpenNoteCommand.ExecuteAsync(page.ProjectNotes[0]);
        notesPage.OnNavigatedTo();
        await notesPage.PendingLoad;

        // Assert
        notesPage.HasProjects.ShouldBeTrue();
        inFilter.ShouldBe(["Общая"]);
        notesPage.SelectedProjectFilter!.Project.ShouldBeNull();
        notesPage.SelectedNote!.Note.Id.ShouldBe(other.Id);
        notesPage.SelectedProject!.Project!.Number.ShouldBe("10");
        (await _notes.GetNotesAsync(projectId: 2, cancellationToken: TestContext.Current.CancellationToken)).ShouldHaveSingleItem().Id.ShouldBe(other.Id);
    }

    [Fact]
    public async Task Sorting_WorkFolderChip_ShowsProjectOfFolderOrParent_UpdatesOnChange()
    {
        // Arrange
        var library = Substitute.For<ILibraryService>();
        library.GetTagsAsync(Arg.Any<CancellationToken>()).Returns([]);
        library.GetAutoTagsAsync(Arg.Any<CancellationToken>()).Returns([]);
        var sorting = new SortingViewModel(
            _sorting,
            Substitute.For<IBrowseService>(),
            new SampleOperationsService(),
            Substitute.For<ISearchService>(),
            library,
            Substitute.For<IRootsService>(),
            clock: () => _now,
            inbox: Substitute.For<IInboxService>(),
            projects: _projects);
        await sorting.RefreshAsync();

        // Act
        var badges = sorting.WorkFolders.Select(static item => item.Project?.Number).ToList();
        await _projects.UpdateAsync(1, new ProjectInput("418", "Школа", "#E74856", Work417), TestContext.Current.CancellationToken);
        await WaitAsync(() => sorting.WorkFolders[0].Project?.Number == "418");

        // Assert: папка проекта и её подпапка — с иконкой, чужая — без; переименование проекта видно сразу
        badges.ShouldBe(["417", "417", null]);
        sorting.WorkFolders[1].Tip.ShouldStartWith("Проект 418 · Школа");
        ProjectInfo.ForPath([new ProjectInfo(1, "1", string.Empty, null, @"D:\Work"), new ProjectInfo(2, "2", string.Empty, null, @"D:\Work\417")], @"D:\Work\417\Акты")!.Number.ShouldBe("2");
        ProjectInfo.ForPath([new ProjectInfo(1, "1", string.Empty, null, @"D:\Work\41")], @"D:\Work\417").ShouldBeNull();
    }

    [Fact]
    public void MainWindow_ProjectsPage_BeforeNotes()
    {
        // Arrange
        var search = new SampleSearchService(() => _now);
        var library = Substitute.For<ILibraryService>();
        var hotkeys = new HotkeyStatus();
        var actions = Substitute.For<IFileActions>();
        var notesPage = new NotesViewModel(_notes, clock: () => _now, projects: _projects);

        // Act
        var main = new MainViewModel(
            new HomeViewModel(search, new DesignData.NullAppCommands(), hotkeys),
            new QuickSearchViewModel(search, actions, () => _now),
            new BrowseViewModel(new SampleBrowseService(() => _now), new SampleRootsService(), search, actions, null, null, () => _now),
            new OperationsViewModel(new SampleOperationsService(), new ImmediateDispatcher()),
            new CollectionsViewModel(library, search, actions, _dialogs, _notices, null, () => _now),
            new TagsViewModel(library, search, actions, _dialogs, _notices, null, () => _now),
            new SortingViewModel(_sorting, Substitute.For<IBrowseService>(), new SampleOperationsService(), search, library, Substitute.For<IRootsService>(), inbox: Substitute.For<IInboxService>()),
            new SettingsViewModel(Substitute.For<IThemeService>(), search, hotkeys),
            search,
            notesPage,
            new ProjectsViewModel(_projects, _notes, notesPage: notesPage));

        // Assert
        main.Pages.Select(static page => page.Key).ShouldBe(
            [PageKeys.Home, PageKeys.Search, PageKeys.Browse, PageKeys.Collections, PageKeys.Tags, PageKeys.Inbox, PageKeys.Projects, PageKeys.Notes, PageKeys.Operations]);
        main.Pages.Single(static page => page.Key == PageKeys.Projects).Title.ShouldBe("Проекты");
    }

    private static async Task WaitAsync(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!done() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private DateTimeOffset Tick()
    {
        _now = _now.AddSeconds(1);
        return _now;
    }

    private async Task<(ProjectsViewModel Page, NotesViewModel Notes)> OpenAsync()
    {
        var notesPage = new NotesViewModel(_notes, _dialogs, _notices, () => _now, projects: _projects);
        var page = new ProjectsViewModel(_projects, _notes, _sorting, _dialogs, _notices, _commands, notesPage);
        page.OnNavigatedTo();
        await page.PendingLoad;
        return (page, notesPage);
    }
}

using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DiskApp.Contracts.Projects;
using DiskApp.UI.Localization;
using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.Text;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.ViewModels;

/// <summary>Строка списка проектов.</summary>
public sealed partial class ProjectItem : ObservableObject
{
    /// <summary>Создаёт строку.</summary>
    public ProjectItem(ProjectInfo project) => Project = project ?? throw new ArgumentNullException(nameof(project));

    /// <summary>Проект.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Number), nameof(Title), nameof(Color), nameof(Details), nameof(NumberText))]
    public partial ProjectInfo Project { get; set; }

    /// <summary>Номер.</summary>
    public string Number => Project.Number;

    /// <summary>Номер для строки; у нового проекта без номера — «Новый проект».</summary>
    public string NumberText => Project.Number.Length > 0 ? Project.Number : L.T("Projects.New");

    /// <summary>Название.</summary>
    public string Title => Project.Title;

    /// <summary>Цвет иконки.</summary>
    public string? Color => Project.Color;

    /// <summary>Папка и заметки: «10S-417 · 3 заметки».</summary>
    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (Project.FolderPath is { Length: > 0 } folder)
            {
                parts.Add(PathText.FileName(folder));
            }

            if (Project.NoteCount > 0)
            {
                parts.Add(ProjectsViewModel.NotesCount(Project.NoteCount));
            }

            return string.Join(" · ", parts);
        }
    }

    /// <inheritdoc />
    public override string ToString() => Project.DisplayName;
}

/// <summary>Цвет иконки в палитре карточки; <c>null</c> — цвет по номеру.</summary>
/// <param name="Color">Цвет <c>#RRGGBB</c>.</param>
/// <param name="Text">Подпись для чтения с экрана.</param>
public sealed record ProjectColorChoice(string? Color, string Text)
{
    /// <summary>Цвет по номеру.</summary>
    public bool IsAuto => Color is null;

    /// <inheritdoc />
    public override string ToString() => Text;
}

/// <summary>
/// Страница «Проекты» (PLAN.md, этап 24, ADR-0017): список по номеру слева, карточка проекта справа — номер, название,
/// цвет иконки, основная папка (из рабочих папок или любая), заметки проекта. Изменения сохраняются сами после паузы;
/// новый проект попадает на сервер, когда у него есть номер. Удаление — в «Ещё», папка и заметки остаются.
/// </summary>
public sealed partial class ProjectsViewModel : PageViewModel, IPageLifecycle
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(600);

    private readonly IProjectsService _projects;
    private readonly INotesService? _notes;
    private readonly ISortingSettings? _sorting;
    private readonly IDialogService? _dialogs;
    private readonly IInfoBarService? _notices;
    private readonly IAppCommands? _commands;
    private readonly NotesViewModel? _notesPage;
    private CancellationTokenSource? _saveDelay;
    private Task _saving = Task.CompletedTask;
    private bool _loading;
    private ProjectItem? _discarded;

    /// <summary>Создаёт страницу.</summary>
    /// <param name="projects">Проекты.</param>
    /// <param name="notes">Заметки: список заметок проекта; <c>null</c> — без него.</param>
    /// <param name="sorting">Рабочие папки «Разбора» — первые в выборе папки проекта.</param>
    /// <param name="dialogs">Диалоги: выбор папки, подтверждение удаления.</param>
    /// <param name="notices">Уведомления об ошибках.</param>
    /// <param name="commands">Переходы: папка на «Обзоре», страница «Заметки».</param>
    /// <param name="notesPage">Страница «Заметки»: открыть заметку проекта или начать новую.</param>
    public ProjectsViewModel(
        IProjectsService projects,
        INotesService? notes = null,
        ISortingSettings? sorting = null,
        IDialogService? dialogs = null,
        IInfoBarService? notices = null,
        IAppCommands? commands = null,
        NotesViewModel? notesPage = null)
        : base(PageKeys.Projects, "Projects.Title", FASymbol.Flag)
    {
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _notes = notes;
        _sorting = sorting;
        _dialogs = dialogs;
        _notices = notices;
        _commands = commands;
        _notesPage = notesPage;
        Colors.Add(new ProjectColorChoice(null, L.T("Projects.AutoColor")));
        foreach (var color in TagPalette.Colors)
        {
            Colors.Add(new ProjectColorChoice(color, color));
        }
    }

    /// <summary>Новый проект создан: поле номера получает фокус.</summary>
    public event EventHandler? NumberFocusRequested;

    /// <summary>Проекты списка (с учётом поиска).</summary>
    public ObservableCollection<ProjectItem> Projects { get; } = [];

    /// <summary>Поиск по номеру и названию.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText))]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>Выбранный проект.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProject))]
    public partial ProjectItem? SelectedProject { get; set; }

    /// <summary>Номер открытого проекта.</summary>
    [ObservableProperty]
    public partial string Number { get; set; } = string.Empty;

    /// <summary>Ошибка номера: пустой, длинный, занят; пусто — ошибки нет.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNumberError))]
    public partial string NumberError { get; set; } = string.Empty;

    /// <summary>Есть ошибка номера.</summary>
    public bool HasNumberError => NumberError.Length > 0;

    /// <summary>Название открытого проекта.</summary>
    [ObservableProperty]
    public partial string ProjectTitle { get; set; } = string.Empty;

    /// <summary>Палитра: «по номеру» и цвета.</summary>
    public ObservableCollection<ProjectColorChoice> Colors { get; } = [];

    /// <summary>Выбранный цвет.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BadgeColor))]
    public partial ProjectColorChoice? SelectedColor { get; set; }

    /// <summary>Цвет иконки в карточке; <c>null</c> — по номеру.</summary>
    public string? BadgeColor => SelectedColor?.Color;

    /// <summary>Основная папка открытого проекта.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFolder), nameof(FolderName), nameof(FolderText))]
    public partial string? FolderPath { get; set; }

    /// <summary>Папка выбрана.</summary>
    public bool HasFolder => FolderPath is { Length: > 0 };

    /// <summary>Имя папки крупно.</summary>
    public string FolderName => FolderPath is { Length: > 0 } folder ? PathText.FileName(folder) : L.T("Projects.NoFolder");

    /// <summary>Путь папки под именем.</summary>
    public string FolderText => FolderPath ?? L.T("Projects.FolderHint");

    /// <summary>Рабочие папки «Разбора» для выбора основной папки.</summary>
    public ObservableCollection<SortingFolderInfo> WorkFolders { get; } = [];

    /// <summary>Рабочие папки есть.</summary>
    [ObservableProperty]
    public partial bool HasWorkFolders { get; set; }

    /// <summary>Заметки открытого проекта.</summary>
    public ObservableCollection<NoteInfo> ProjectNotes { get; } = [];

    /// <summary>У проекта есть заметки.</summary>
    [ObservableProperty]
    public partial bool HasProjectNotes { get; set; }

    /// <summary>Заметки можно показывать и создавать.</summary>
    public bool CanUseNotes => _notes is not null;

    /// <summary>Проект открыт.</summary>
    public bool HasProject => SelectedProject is not null;

    /// <summary>Список пуст.</summary>
    public bool IsEmpty => Projects.Count == 0;

    /// <summary>Подпись вместо пустого списка.</summary>
    public string EmptyText => SearchText.Trim().Length > 0 ? L.T("Projects.NothingFound") : L.T("Projects.Empty");

    /// <summary>Завершается, когда закончилось последнее чтение (тесты).</summary>
    public Task PendingLoad { get; private set; } = Task.CompletedTask;

    private List<ProjectInfo> All { get; set; } = [];

    /// <summary>«3 заметки».</summary>
    public static string NotesCount(int count) => Formatting.Count(count, L.T("Projects.Notes.One"), L.T("Projects.Notes.Few"), L.T("Projects.Notes.Many"));

    /// <summary>Проблема номера: пустой, длинный или занят другим проектом; <c>null</c> — номер подходит.</summary>
    public static string? NumberProblem(string number, IEnumerable<ProjectInfo> others)
    {
        ArgumentNullException.ThrowIfNull(number);
        ArgumentNullException.ThrowIfNull(others);

        var trimmed = number.Trim();
        if (trimmed.Length == 0)
        {
            return L.T("Projects.NumberRequired");
        }

        if (trimmed.Length > ProjectNumbers.MaxLength)
        {
            return L.F("Projects.NumberTooLong", ProjectNumbers.MaxLength);
        }

        return others.FirstOrDefault(project => ProjectNumbers.Same(project.Number, trimmed)) is { } taken
            ? L.F("Projects.NumberTakenBy", trimmed, taken.DisplayName)
            : null;
    }

    /// <inheritdoc />
    public void OnNavigatedTo() => PendingLoad = ReloadAsync(SelectedProject?.Project.Id);

    /// <inheritdoc />
    public void OnNavigatedFrom() => _ = FlushAsync();

    /// <summary>Сохраняет отложенную правку сразу.</summary>
    public Task FlushAsync()
    {
        Interlocked.Exchange(ref _saveDelay, null)?.Cancel();
        return SaveQueuedAsync(Capture(SelectedProject));
    }

    /// <inheritdoc />
    protected override Task RelocalizeAsync()
    {
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(FolderName));
        OnPropertyChanged(nameof(FolderText));
        return ReloadAsync(SelectedProject?.Project.Id);
    }

    /// <summary>Новый проект: сразу в правке, на сервер попадёт с номером.</summary>
    [RelayCommand]
    private async Task NewProjectAsync()
    {
        await FlushAsync();
        if (SearchText.Length > 0)
        {
            SearchText = string.Empty;
        }

        var draft = new ProjectItem(new ProjectInfo(0, string.Empty, string.Empty, null, null));
        Projects.Insert(0, draft);
        OnPropertyChanged(nameof(IsEmpty));
        SelectedProject = draft;
        NumberFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Основная папка — одна из рабочих папок.</summary>
    [RelayCommand]
    private void ChooseFolder(string? path)
    {
        if (!HasProject || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        FolderPath = path;

        // Номер часто — имя папки («10S-417»): подставляется, если ещё пуст и помещается.
        var name = PathText.FileName(path);
        if (Number.Trim().Length == 0 && name.Length is > 0 and <= ProjectNumbers.MaxLength)
        {
            Number = name;
        }
    }

    /// <summary>Основная папка — любая, через диалог выбора.</summary>
    [RelayCommand]
    private async Task BrowseFolderAsync()
    {
        if (_dialogs is not null && HasProject && await _dialogs.PickFolderAsync(L.T("Projects.PickFolderTitle")) is { } path)
        {
            ChooseFolder(path);
        }
    }

    /// <summary>Отвязать папку: папка остаётся на диске.</summary>
    [RelayCommand]
    private void ClearFolder() => FolderPath = null;

    /// <summary>Открыть основную папку на «Обзоре».</summary>
    [RelayCommand]
    private void OpenFolder()
    {
        if (FolderPath is { Length: > 0 } folder)
        {
            _commands?.BrowseFolder(folder);
        }
    }

    /// <summary>Новая заметка проекта — на странице «Заметки».</summary>
    [RelayCommand]
    private async Task NewNoteAsync()
    {
        await FlushAsync();
        if (SelectedProject?.Project is { Id: > 0 } project && _notesPage is not null)
        {
            _notesPage.NewNoteOnNavigate(project.Id);
            _commands?.NavigateTo(PageKeys.Notes);
        }
    }

    /// <summary>Открыть заметку проекта на странице «Заметки».</summary>
    [RelayCommand]
    private async Task OpenNoteAsync(NoteInfo? note)
    {
        await FlushAsync();
        if (note is not null && _notesPage is not null)
        {
            _notesPage.OpenOnNavigate(note.Id);
            _commands?.NavigateTo(PageKeys.Notes);
        }
    }

    /// <summary>Удалить проект после подтверждения: папка и заметки остаются.</summary>
    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedProject is not { } item)
        {
            return;
        }

        Interlocked.Exchange(ref _saveDelay, null)?.Cancel();
        await _saving;
        if (item.Project.Id == 0)
        {
            RemoveDraft(item);
            return;
        }

        if (_dialogs is not null
            && !await _dialogs.ConfirmAsync(L.F("Projects.DeleteTitle", item.Project.DisplayName), L.T("Projects.DeleteMessage"), L.T("Projects.Delete")))
        {
            return;
        }

        if (await TryAsync(() => _projects.DeleteAsync(item.Project.Id)))
        {
            await ReloadAsync(null);
        }
    }

    partial void OnSelectedProjectChanged(ProjectItem? oldValue, ProjectItem? newValue)
    {
        // Правка прежнего проекта сохраняется снимком полей, снятым до того, как поля покажут новый.
        if (oldValue is not null && !ReferenceEquals(oldValue, newValue))
        {
            Interlocked.Exchange(ref _saveDelay, null)?.Cancel();
            var pending = Capture(oldValue);
            _ = SaveQueuedAsync(pending);
            if (oldValue.Project.Id == 0 && pending?.Input.Number.Trim().Length == 0 && Projects.Contains(oldValue))
            {
                // Новый проект без номера никуда не сохраняется — просто исчезает.
                Projects.Remove(oldValue);
                OnPropertyChanged(nameof(IsEmpty));
            }
        }

        _loading = true;
        try
        {
            var project = newValue?.Project;
            Number = project?.Number ?? string.Empty;
            ProjectTitle = project?.Title ?? string.Empty;
            SelectedColor = Colors.FirstOrDefault(choice => string.Equals(choice.Color, project?.Color, StringComparison.OrdinalIgnoreCase)) ?? Colors[0];
            FolderPath = project?.FolderPath;
            NumberError = string.Empty;
        }
        finally
        {
            _loading = false;
        }

        _ = LoadNotesAsync(newValue?.Project);
    }

    partial void OnNumberChanged(string value)
    {
        if (!_loading)
        {
            NumberError = NumberProblem(value, Others(SelectedProject)) ?? string.Empty;
        }

        QueueSave();
    }

    partial void OnProjectTitleChanged(string value) => QueueSave();

    partial void OnSelectedColorChanged(ProjectColorChoice? value) => QueueSave();

    partial void OnFolderPathChanged(string? value) => QueueSave();

    partial void OnSearchTextChanged(string value)
    {
        if (!_loading)
        {
            ShowList(SelectedProject?.Project.Id);
        }
    }

    private IEnumerable<ProjectInfo> Others(ProjectItem? item) => All.Where(project => project.Id != item?.Project.Id);

    private void QueueSave()
    {
        if (_loading || SelectedProject is null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _saveDelay, cts)?.Cancel();
        _ = SaveAfterAsync(cts.Token);
    }

    private async Task SaveAfterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SaveDelay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await SaveQueuedAsync(Capture(SelectedProject));
    }

    private SaveRequest? Capture(ProjectItem? item)
        => item is null || ReferenceEquals(item, _discarded) ? null : new SaveRequest(item, new ProjectInput(Number.Trim(), ProjectTitle.Trim(), SelectedColor?.Color, FolderPath));

    private Task SaveQueuedAsync(SaveRequest? request)
    {
        var next = SaveAfterPreviousAsync(_saving, request);
        _saving = next;
        return next;
    }

    private async Task SaveAfterPreviousAsync(Task previous, SaveRequest? request)
    {
        await previous;
        if (request is null)
        {
            return;
        }

        var project = request.Item.Project;
        var input = request.Input;
        if (input == new ProjectInput(project.Number, project.Title, project.Color, project.FolderPath)
            || NumberProblem(input.Number, Others(request.Item)) is not null)
        {
            // Без изменений или номер не подходит — подсказка уже под полем.
            return;
        }

        await TryAsync(async () =>
        {
            var saved = project.Id == 0 ? await _projects.CreateAsync(input) : await _projects.UpdateAsync(project.Id, input);
            request.Item.Project = saved with { NoteCount = project.NoteCount };
            All = [.. All.Where(existing => existing.Id != saved.Id), request.Item.Project];
        });
    }

    private async Task LoadNotesAsync(ProjectInfo? project)
    {
        ProjectNotes.Clear();
        HasProjectNotes = false;
        if (_notes is null || project is not { Id: > 0 })
        {
            return;
        }

        IReadOnlyList<NoteInfo> list = [];
        if (await TryAsync(async () => list = await _notes.GetNotesAsync(projectId: project.Id)) && SelectedProject?.Project.Id == project.Id)
        {
            foreach (var note in list)
            {
                ProjectNotes.Add(note);
            }

            HasProjectNotes = ProjectNotes.Count > 0;
        }
    }

    private async Task ReloadAsync(long? selectId)
    {
        await FlushAsync();
        if (selectId == 0)
        {
            // Новый проект мог только что сохраниться и получить номер — выделение остаётся на нём.
            selectId = SelectedProject?.Project.Id;
        }

        await TryAsync(async () =>
        {
            All = [.. await _projects.GetProjectsAsync()];
            if (_sorting is not null)
            {
                var folders = await _sorting.GetAsync();
                WorkFolders.Clear();
                foreach (var folder in folders.WorkFolders)
                {
                    WorkFolders.Add(folder);
                }

                HasWorkFolders = WorkFolders.Count > 0;
            }
        });

        ShowList(selectId);
    }

    private void ShowList(long? selectId)
    {
        var terms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var draft = Projects.FirstOrDefault(static item => item.Project.Id == 0);
        Projects.Clear();
        if (draft is not null && terms.Length == 0)
        {
            Projects.Add(draft);
        }

        foreach (var project in All
            .Where(project => terms.All(term => project.Number.Contains(term, StringComparison.CurrentCultureIgnoreCase) || project.Title.Contains(term, StringComparison.CurrentCultureIgnoreCase)))
            .OrderBy(static project => project.Number, Comparer<string>.Create(ProjectNumbers.Compare)))
        {
            Projects.Add(new ProjectItem(project));
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
        SelectedProject = Projects.FirstOrDefault(item => item.Project.Id == selectId && selectId is not 0) ?? (ReferenceEquals(SelectedProject, draft) && draft is not null && Projects.Contains(draft) ? draft : Projects.FirstOrDefault());
    }

    private void RemoveDraft(ProjectItem item)
    {
        // Удаляемый новый проект не сохраняется при смене выделения, даже если номер уже введён.
        _discarded = item;
        try
        {
            Projects.Remove(item);
            SelectedProject = Projects.FirstOrDefault();
        }
        finally
        {
            _discarded = null;
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    private async Task<bool> TryAsync(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show(L.T("Projects.Title"), L.F("Projects.Failed", exception.Message), FAInfoBarSeverity.Error);
            return false;
        }
    }

    private sealed record SaveRequest(ProjectItem Item, ProjectInput Input);
}

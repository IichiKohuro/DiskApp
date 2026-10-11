using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DiskApp.UI.Localization;
using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.Text;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.ViewModels;

/// <summary>Папка, где тег — авто-тег (PLAN.md, этап 19).</summary>
/// <param name="Path">Полный путь.</param>
/// <param name="Name">Имя для кнопки.</param>
public sealed record AutoTagFolder(string Path, string Name);

/// <summary>
/// Страница «Теги» (PLAN.md, этап 8.7): список тегов с цветами, создание, переименование, смена цвета и удаление;
/// файлы выбранного тега — поиском <c>tag:</c>.
/// </summary>
public sealed partial class TagsViewModel : PageViewModel, IPageLifecycle
{
    private readonly ILibraryService _library;
    private readonly ISearchService _search;
    private readonly IFileActions _actions;
    private readonly IDialogService? _dialogs;
    private readonly IInfoBarService? _notices;
    private readonly Func<DateTimeOffset> _clock;
    private readonly IAppCommands? _commands;
    private int _filesVersion;

    /// <summary>
    /// Создаёт страницу приложения.
    /// </summary>
    public TagsViewModel(ILibraryService library, ISearchService search, IFileActions actions, IDialogService dialogs, IInfoBarService notices, ITagEditor? tagEditor = null, IAppCommands? commands = null)
        : this(library, search, actions, dialogs, notices, tagEditor, static () => DateTimeOffset.Now, commands)
    {
    }

    /// <summary>
    /// Создаёт страницу с заданными часами (тесты, превью).
    /// </summary>
    public TagsViewModel(ILibraryService library, ISearchService search, IFileActions actions, IDialogService? dialogs, IInfoBarService? notices, ITagEditor? tagEditor, Func<DateTimeOffset> clock, IAppCommands? commands = null)
        : base(PageKeys.Tags, "Common.Page.Tags", FASymbol.Tag)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(clock);

        _library = library;
        _search = search;
        _actions = actions;
        _dialogs = dialogs;
        _notices = notices;
        TagEditor = tagEditor;
        _clock = clock;
        _commands = commands;
    }

    /// <summary>Диалог тегов файла.</summary>
    public ITagEditor? TagEditor { get; }

    /// <summary>Теги можно менять у файлов.</summary>
    public bool CanEditFileTags => TagEditor is not null;

    /// <summary>Все теги.</summary>
    public ObservableCollection<TagInfo> Tags { get; } = [];

    /// <summary>Файлы выбранного тега.</summary>
    public ObservableCollection<SearchHit> Files { get; } = [];

    /// <summary>Выбранный тег.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(FilesTitle))]
    [NotifyCanExecuteChangedFor(nameof(EditTagCommand), nameof(DeleteTagCommand))]
    public partial TagInfo? SelectedTag { get; set; }

    /// <summary>Папки, где выбранный тег — авто-тег (этап 19): их файлы входят в список ниже.</summary>
    public ObservableCollection<AutoTagFolder> AutoTagFolders { get; } = [];

    /// <summary>Выбранный тег — авто-тег хотя бы одной папки.</summary>
    public bool HasAutoTagFolders => AutoTagFolders.Count > 0;

    /// <summary>Выбранный файл.</summary>
    [ObservableProperty]
    public partial SearchHit? SelectedFile { get; set; }

    /// <summary>Идёт загрузка.</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>Подпись под списком файлов.</summary>
    [ObservableProperty]
    public partial string FilesText { get; set; } = string.Empty;

    /// <summary>Ошибка загрузки; пусто — всё хорошо.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string Error { get; set; } = string.Empty;

    /// <summary>Есть ошибка.</summary>
    public bool HasError => Error.Length > 0;

    /// <summary>Тег выбран.</summary>
    public bool HasSelection => SelectedTag is not null;

    /// <summary>Тегов нет.</summary>
    public bool IsEmpty => Tags.Count == 0 && !IsLoading;

    /// <summary>Заголовок правой части.</summary>
    public string FilesTitle => SelectedTag is { } tag ? L.F("Tags.FilesTagged", tag.Name) : L.T("Tags.ChooseATag");

    /// <summary>Завершается, когда загружены теги и файлы выбранного.</summary>
    public Task PendingLoad { get; private set; } = Task.CompletedTask;

    /// <inheritdoc />
    public void OnNavigatedTo() => PendingLoad = RefreshAsync();

    /// <inheritdoc />
    public void OnNavigatedFrom()
    {
    }

    /// <inheritdoc />
    protected override Task RelocalizeAsync() => RefreshAsync();

    /// <summary>Перечитывает теги и файлы выбранного тега.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true;
        var before = SelectedTag;
        try
        {
            var tags = await _library.GetTagsAsync();
            var selected = SelectedTag?.Id;
            Tags.Clear();
            foreach (var tag in tags)
            {
                Tags.Add(tag);
            }

            SelectedTag = Tags.FirstOrDefault(tag => tag.Id == selected) ?? Tags.FirstOrDefault();
            Error = string.Empty;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Error = L.T("Tags.CouldNotLoadTags") + exception.Message;
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }

        // Смена выбранного тега уже загружает файлы; тот же тег (запись сравнивается по значению) — перечитываем здесь.
        if (Equals(before, SelectedTag))
        {
            await LoadFilesAsync();
        }
        else
        {
            await PendingLoad;
        }
    }

    partial void OnSelectedTagChanged(TagInfo? value) => PendingLoad = LoadFilesAsync();

    private async Task LoadFilesAsync()
    {
        var version = ++_filesVersion;
        if (SelectedTag is not { } tag)
        {
            Files.Clear();
            AutoTagFolders.Clear();
            OnPropertyChanged(nameof(HasAutoTagFolders));
            FilesText = string.Empty;
            return;
        }

        await LoadAutoTagFoldersAsync(tag, version);

        try
        {
            var result = await _search.SearchAsync("tag:" + QueryText.Quote(tag.Name), CancellationToken.None);
            if (version != _filesVersion)
            {
                return;
            }

            var now = _clock();
            Files.Clear();
            foreach (var entry in result.Entries)
            {
                Files.Add(new SearchHit(entry, string.Empty, 0, now));
            }

            FilesText = result.TotalCount == 0
                ? L.T("Tags.NoFilesHaveThisTagTag")
                : Formatting.Count(result.TotalCount, L.T("Common.File.One"), L.T("Common.File.Few"), L.T("Common.File.Many"));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Files.Clear();
            FilesText = L.T("Tags.CouldNotFindFiles") + exception.Message;
        }
    }

    /// <summary>Папки, где тег — авто-тег; без ответа сервера список просто пуст.</summary>
    private async Task LoadAutoTagFoldersAsync(TagInfo tag, int version)
    {
        IReadOnlyList<FolderTagsInfo> folders = [];
        try
        {
            folders = await _library.GetAutoTagsAsync() ?? [];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _ = exception;
        }

        if (version != _filesVersion)
        {
            return;
        }

        AutoTagFolders.Clear();
        foreach (var folder in folders.Where(folder => folder.Tags.Any(candidate => candidate.Id == tag.Id)).OrderBy(static folder => folder.Path, StringComparer.CurrentCultureIgnoreCase))
        {
            AutoTagFolders.Add(new AutoTagFolder(folder.Path, PathText.FileName(folder.Path)));
        }

        OnPropertyChanged(nameof(HasAutoTagFolders));
    }

    /// <summary>Открыть папку с авто-тегом в «Обзоре»: там авто-теги меняются и снимаются.</summary>
    [RelayCommand]
    private void OpenAutoTagFolder(AutoTagFolder? folder)
    {
        if (folder is not null)
        {
            _commands?.BrowseFolder(folder.Path);
        }
    }

    /// <summary>Новый тег: имя и цвет.</summary>
    [RelayCommand]
    private async Task CreateTagAsync()
    {
        if (_dialogs is null)
        {
            return;
        }

        var color = TagPalette.Next(Tags.Select(static tag => tag.Color));
        if (await _dialogs.EditTagAsync(L.T("Tags.NewTag"), string.Empty, color, L.T("Common.Create")) is not { } input)
        {
            return;
        }

        try
        {
            var tag = await _library.CreateTagAsync(input.Name, input.Color);
            await RefreshAsync();
            SelectedTag = Tags.FirstOrDefault(candidate => candidate.Id == tag.Id);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show(L.T("Tags.CouldNotCreateTheTag"), exception.Message, FAInfoBarSeverity.Error);
        }
    }

    private bool CanChangeTag() => SelectedTag is not null;

    /// <summary>Переименовать тег или сменить цвет.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeTag))]
    private async Task EditTagAsync()
    {
        if (_dialogs is null || SelectedTag is not { } tag)
        {
            return;
        }

        if (await _dialogs.EditTagAsync(L.T("Tags.EditTag"), tag.Name, tag.Color, L.T("Common.Save")) is not { } input)
        {
            return;
        }

        try
        {
            await _library.UpdateTagAsync(tag.Id, input.Name, input.Color);
            await RefreshAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show(L.T("Tags.CouldNotChangeTheTag"), exception.Message, FAInfoBarSeverity.Error);
        }
    }

    /// <summary>Удалить тег у всех файлов; файлы остаются.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeTag))]
    private async Task DeleteTagAsync()
    {
        if (_dialogs is null || SelectedTag is not { } tag)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync(L.T("Tags.DeleteTag"), L.F("Tags.DeleteTagItIsRemovedFrom", tag.Name), L.T("Common.Delete")))
        {
            return;
        }

        try
        {
            await _library.DeleteTagAsync(tag.Id);
            SelectedTag = null;
            await RefreshAsync();
            _notices?.Show(L.T("Tags.TagDeleted"), $"«{tag.Name}»", FAInfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show(L.T("Tags.CouldNotDeleteTheTag"), exception.Message, FAInfoBarSeverity.Error);
        }
    }

    /// <summary>Открыть файл.</summary>
    [RelayCommand]
    private async Task OpenFileAsync(SearchHit? hit)
    {
        if ((hit ?? SelectedFile) is not { } target)
        {
            return;
        }

        try
        {
            await _search.OpenAsync(target.Entry, null, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show(L.T("Tags.CouldNotOpen"), exception.Message, FAInfoBarSeverity.Error);
        }
    }

    /// <summary>Показать файл в Проводнике.</summary>
    [RelayCommand]
    private async Task RevealFileAsync()
    {
        if (SelectedFile is not { } hit)
        {
            return;
        }

        try
        {
            await _actions.RevealAsync(hit.Entry);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show(L.T("Tags.CouldNotShowInFolder"), exception.Message, FAInfoBarSeverity.Error);
        }
    }

    /// <summary>Теги выбранного файла.</summary>
    [RelayCommand]
    private async Task EditFileTagsAsync()
    {
        if (SelectedFile is not { } hit || TagEditor is null)
        {
            return;
        }

        try
        {
            if (await TagEditor.EditAsync([hit.Entry]) is { Count: > 0 })
            {
                await LoadFilesAsync();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show(L.T("Tags.CouldNotChangeTags"), exception.Message, FAInfoBarSeverity.Error);
        }
    }
}

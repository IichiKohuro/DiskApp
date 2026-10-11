using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DiskApp.UI.Localization;
using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.Text;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.ViewModels;

/// <summary>Склад в фильтре над списком файлов; «Все склады» — без папки.</summary>
public sealed partial class InboxFilter : ObservableObject
{
    /// <summary>Создаёт фильтр.</summary>
    public InboxFilter(SortingFolderInfo? folder, string title)
    {
        Folder = folder;
        Title = title;
    }

    /// <summary>Склад; <c>null</c> — все склады.</summary>
    public SortingFolderInfo? Folder { get; }

    /// <summary>Подпись.</summary>
    public string Title { get; }

    /// <summary>Сколько файлов ждёт разбора.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    public partial int Count { get; set; }

    /// <summary>Склад не прочитан: вне индекса или ещё сканируется.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    public partial string? Problem { get; set; }

    /// <summary>Подпись с числом файлов.</summary>
    public string Text => Problem is null ? $"{Title} · {Count}" : $"{Title} · !";

    /// <inheritdoc />
    public override string ToString() => Text;
}

/// <summary>Файл или папка со склада.</summary>
/// <param name="Entry">Запись индекса.</param>
/// <param name="Inbox">Склад, где лежит.</param>
public sealed record SortItem(FileEntry Entry, SortingFolderInfo Inbox)
{
    /// <summary>Когда изменён — для строки списка.</summary>
    public string ModifiedText => Formatting.RelativeTime(Entry.Modified, DateTimeOffset.Now);

    /// <summary>Значок по виду файла, пока нет иконки Windows.</summary>
    public FluentIcons.Common.Symbol Symbol => FileKindSymbols.For(Entry.Kind);

    /// <summary>Размер — для строки списка; у папки пусто.</summary>
    public string SizeText => Entry.Kind == FileKind.Folder ? string.Empty : Formatting.Size(Entry.Size);

    /// <inheritdoc />
    public override string ToString() => Entry.Name;
}

/// <summary>Рабочая папка с клавишей 1–9.</summary>
/// <param name="Number">Номер клавиши; 0 — без клавиши (десятая и дальше).</param>
/// <param name="Folder">Папка.</param>
public sealed record WorkFolderItem(int Number, SortingFolderInfo Folder)
{
    /// <summary>Подпись кнопки: «2 417».</summary>
    public string Text => Number > 0 ? $"{Number} {Folder.Title}" : Folder.Title;

    /// <inheritdoc />
    public override string ToString() => Text;
}

/// <summary>
/// Папка в дереве назначения. Подпапки читаются при раскрытии; новая папка существует только в дереве, пока в неё
/// ничего не перенесли.
/// </summary>
public sealed partial class SortFolderNode : ObservableObject
{
    private readonly Func<SortFolderNode, Task>? _load;
    private bool _loaded;

    /// <summary>Создаёт узел.</summary>
    public SortFolderNode(string name, string path, SortFolderNode? parent, Func<SortFolderNode, Task>? load, bool isNew = false, bool isPlaceholder = false)
    {
        Name = name;
        Path = path;
        Parent = parent;
        IsNew = isNew;
        IsPlaceholder = isPlaceholder;
        _load = load;
        _loaded = isNew || isPlaceholder || load is null;
        if (!_loaded)
        {
            // Заглушка показывает стрелку раскрытия, пока подпапки не прочитаны.
            Children.Add(new SortFolderNode(L.T("Sorting.Loading"), string.Empty, this, null, isPlaceholder: true));
        }
    }

    /// <summary>Имя.</summary>
    public string Name { get; }

    /// <summary>Полный путь.</summary>
    public string Path { get; }

    /// <summary>Родитель; у корня дерева — <c>null</c>.</summary>
    public SortFolderNode? Parent { get; }

    /// <summary>Новая папка: будет создана при переносе.</summary>
    public bool IsNew { get; }

    /// <summary>Заглушка «загрузка…».</summary>
    public bool IsPlaceholder { get; }

    /// <summary>Подпись в дереве.</summary>
    public string Text => IsNew ? L.F("Sorting.NewFolderNode", Name) : Name;

    /// <summary>Подпапки.</summary>
    public ObservableCollection<SortFolderNode> Children { get; } = [];

    /// <summary>Раскрыта в дереве.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>Завершается, когда подпапки прочитаны.</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>Читает подпапки, если ещё не читал.</summary>
    public Task EnsureLoadedAsync()
    {
        if (_loaded || _load is null)
        {
            return Loading;
        }

        _loaded = true;
        Loading = _load(this);
        return Loading;
    }

    /// <summary>Заменяет заглушку прочитанными подпапками; новые папки, добавленные пользователем, остаются.</summary>
    public void SetChildren(IEnumerable<SortFolderNode> children)
    {
        var added = Children.Where(static child => child.IsNew).ToArray();
        Children.Clear();
        foreach (var child in children.Concat(added))
        {
            Children.Add(child);
        }
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            _ = EnsureLoadedAsync();
        }
    }

    /// <inheritdoc />
    public override string ToString() => Text;
}

/// <summary>
/// Страница «Разбор» (PLAN.md, этап 17): файлы со складов («Загрузки», полученные в мессенджере) раскладываются по
/// рабочим папкам — в подпапку, при необходимости новую, с тегами. Перенос — операция общей очереди, отменяется из
/// уведомления. Склады и рабочие папки настраиваются здесь же; папка вне индекса добавляется в него, чтобы поиск
/// находил и склады, и рабочие папки.
/// </summary>
public sealed partial class SortingViewModel : PageViewModel, IPageLifecycle
{
    private readonly ISortingSettings _settings;
    private readonly IBrowseService _browse;
    private readonly IOperationsService _operations;
    private readonly ISearchService _search;
    private readonly ILibraryService _library;
    private readonly IRootsService _roots;
    private readonly IDialogService? _dialogs;
    private readonly IInfoBarService? _notices;
    private readonly List<SortItem> _all = [];
    private IReadOnlyList<SortItem> _selection = [];
    private IReadOnlyList<string> _allTags = [];
    private SortingFolders _folders = SortingFolders.Empty;
    private CancellationTokenSource? _folderSearch;

    /// <summary>
    /// Создаёт страницу.
    /// </summary>
    public SortingViewModel(
        ISortingSettings settings,
        IBrowseService browse,
        IOperationsService operations,
        ISearchService search,
        ILibraryService library,
        IRootsService roots,
        IDialogService? dialogs = null,
        IInfoBarService? notices = null)
        : base(PageKeys.Inbox, "Sorting.Title", FASymbol.Download)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _browse = browse ?? throw new ArgumentNullException(nameof(browse));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _dialogs = dialogs;
        _notices = notices;
    }

    /// <summary>Склады с числом файлов и «Все склады».</summary>
    public ObservableCollection<InboxFilter> Inboxes { get; } = [];

    /// <summary>Файлы выбранного склада, новые сверху.</summary>
    public ObservableCollection<SortItem> Items { get; } = [];

    /// <summary>Рабочие папки в порядке клавиш.</summary>
    public ObservableCollection<WorkFolderItem> WorkFolders { get; } = [];

    /// <summary>Найденные папки по <see cref="FolderQuery"/>.</summary>
    public ObservableCollection<SortingFolderInfo> FolderResults { get; } = [];

    /// <summary>Дерево назначения: выбранная рабочая папка и её подпапки.</summary>
    public ObservableCollection<SortFolderNode> Tree { get; } = [];

    /// <summary>Теги для переносимого.</summary>
    public ObservableCollection<string> Tags { get; } = [];

    /// <summary>Подходящие теги по <see cref="TagText"/>.</summary>
    public ObservableCollection<string> TagSuggestions { get; } = [];

    /// <summary>Выбранный склад; «Все склады» — первый.</summary>
    [ObservableProperty]
    public partial InboxFilter? SelectedInbox { get; set; }

    /// <summary>Выбранная рабочая папка.</summary>
    [ObservableProperty]
    public partial WorkFolderItem? SelectedWorkFolder { get; set; }

    /// <summary>Папка назначения в дереве.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DestinationText))]
    [NotifyCanExecuteChangedFor(nameof(MoveCommand), nameof(StartNewFolderCommand))]
    public partial SortFolderNode? SelectedFolder { get; set; }

    /// <summary>Поиск любой папки по имени.</summary>
    [ObservableProperty]
    public partial string FolderQuery { get; set; } = string.Empty;

    /// <summary>Имя новой подпапки, пока его вводят.</summary>
    [ObservableProperty]
    public partial string NewFolderName { get; set; } = string.Empty;

    /// <summary>Идёт ввод имени новой подпапки.</summary>
    [ObservableProperty]
    public partial bool IsNamingFolder { get; set; }

    /// <summary>Вводимый тег.</summary>
    [ObservableProperty]
    public partial string TagText { get; set; } = string.Empty;

    /// <summary>Сколько файлов выбрано.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoveText))]
    [NotifyCanExecuteChangedFor(nameof(MoveCommand))]
    public partial int SelectedCount { get; set; }

    /// <summary>Подпись под списком: сколько файлов и сколько выбрано.</summary>
    [ObservableProperty]
    public partial string ItemsText { get; set; } = string.Empty;

    /// <summary>Подсказка вместо пустого списка; пусто — файлы есть.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmptyHint))]
    public partial string EmptyHint { get; set; } = string.Empty;

    /// <summary>Складов ещё нет: показывается приглашение добавить.</summary>
    [ObservableProperty]
    public partial bool HasNoInboxes { get; set; }

    /// <summary>Рабочих папок ещё нет.</summary>
    [ObservableProperty]
    public partial bool HasNoWorkFolders { get; set; }

    /// <summary>Идёт чтение складов.</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>Подсказка видна.</summary>
    public bool HasEmptyHint => EmptyHint.Length > 0;

    /// <summary>Куда пойдут файлы: «417 › Переписка».</summary>
    public string DestinationText => SelectedFolder is { } folder ? DescribePath(folder) : L.T("Sorting.ChooseDestination");

    /// <summary>Подпись кнопки переноса.</summary>
    public string MoveText => SelectedCount > 0
        ? L.F("Sorting.MoveItems", Formatting.Count(SelectedCount, L.T("Common.File.One"), L.T("Common.File.Few"), L.T("Common.File.Many")))
        : L.T("Sorting.Move");

    /// <summary>Завершается, когда закончилась последняя загрузка (для тестов).</summary>
    public Task PendingLoad { get; private set; } = Task.CompletedTask;

    /// <inheritdoc />
    public void OnNavigatedTo() => PendingLoad = RefreshAsync();

    /// <inheritdoc />
    public void OnNavigatedFrom()
    {
    }

    /// <summary>
    /// Перечитывает склады, рабочие папки и теги.
    /// </summary>
    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            _folders = await _settings.GetAsync();
            ShowWorkFolders();
            await LoadInboxesAsync();
            await LoadTagsAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show(L.T("Sorting.Title"), L.F("Sorting.LoadFailed", exception.Message), FAInfoBarSeverity.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Выбор в списке файлов (из представления: множественный выбор <c>ListBox</c>).</summary>
    public void SetSelection(IReadOnlyList<SortItem> items)
    {
        _selection = items ?? [];
        SelectedCount = _selection.Count;
        UpdateItemsText();
    }

    /// <summary>Выбирает рабочую папку по клавише 1–9.</summary>
    [RelayCommand]
    private void SelectWorkFolderByNumber(int number)
    {
        if (WorkFolders.FirstOrDefault(item => item.Number == number) is { } item)
        {
            SelectedWorkFolder = item;
        }
    }

    /// <summary>Делает найденную папку назначением.</summary>
    [RelayCommand]
    private void ChooseFoundFolder(SortingFolderInfo? folder)
    {
        if (folder is null)
        {
            return;
        }

        SelectedWorkFolder = WorkFolders.FirstOrDefault(item => SamePath(item.Folder.Path, folder.Path));
        if (SelectedWorkFolder is null)
        {
            ShowTree(folder);
        }

        FolderQuery = string.Empty;
    }

    /// <summary>Начинает ввод имени новой подпапки в выбранной папке.</summary>
    [RelayCommand(CanExecute = nameof(CanStartNewFolder))]
    private void StartNewFolder()
    {
        NewFolderName = string.Empty;
        IsNamingFolder = true;
    }

    private bool CanStartNewFolder() => SelectedFolder is { IsNew: false };

    /// <summary>Добавляет новую подпапку в дерево и выбирает её; на диске она появится при переносе.</summary>
    [RelayCommand]
    private void ConfirmNewFolder()
    {
        var name = NewFolderName.Trim();
        IsNamingFolder = false;
        if (name.Length == 0 || SelectedFolder is not { IsNew: false } parent)
        {
            return;
        }

        if (FolderNameProblem(name) is { } problem)
        {
            _notices?.Show(L.T("Sorting.NewFolder"), problem, FAInfoBarSeverity.Warning);
            return;
        }

        var existing = parent.Children.FirstOrDefault(child => !child.IsPlaceholder && string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new SortFolderNode(name, System.IO.Path.Combine(parent.Path, name), parent, null, isNew: true);
            parent.Children.Add(existing);
        }

        parent.IsExpanded = true;
        SelectedFolder = existing;
    }

    /// <summary>Отменяет ввод имени новой подпапки.</summary>
    [RelayCommand]
    private void CancelNewFolder() => IsNamingFolder = false;

    /// <summary>Добавляет тег из поля ввода (или подсказку).</summary>
    [RelayCommand]
    private void AddTag(string? text)
    {
        var tag = (text ?? TagText).Trim().TrimStart('#');
        if (tag.Length > 0 && !Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
        {
            Tags.Add(tag);
        }

        TagText = string.Empty;
    }

    /// <summary>Убирает тег.</summary>
    [RelayCommand]
    private void RemoveTag(string? tag)
    {
        if (tag is not null)
        {
            Tags.Remove(tag);
        }
    }

    /// <summary>Переносит выбранные файлы в выбранную папку с тегами.</summary>
    [RelayCommand(CanExecute = nameof(CanMove))]
    private async Task MoveAsync()
    {
        if (SelectedFolder is not { } target || _selection.Count == 0)
        {
            return;
        }

        if (TagText.Trim().Length > 0)
        {
            AddTag(TagText);
        }

        var (destination, newFolder) = target.IsNew ? (target.Parent!.Path, (string?)target.Name) : (target.Path, (string?)null);
        var items = _selection.ToArray();
        var tags = Tags.ToArray();
        try
        {
            var operation = await _operations.SortAsync(new SortOperation([.. items.Select(static item => item.Entry.FullPath)], destination, newFolder, tags));
            foreach (var item in items)
            {
                _all.Remove(item);
                Items.Remove(item);
            }

            SetSelection([]);
            UpdateCounts();
            Tags.Clear();
            if (target.IsNew)
            {
                // Папка создана: в дереве она теперь обычная.
                _ = ReloadChildrenAsync(target.Parent!);
            }

            var what = items.Length == 1 ? $"«{items[0].Entry.Name}»" : Formatting.Count(items.Length, L.T("Common.File.One"), L.T("Common.File.Few"), L.T("Common.File.Many"));
            var message = tags.Length > 0 ? L.F("Sorting.MovedWithTags", what, DescribePath(target), string.Join(", ", tags)) : L.F("Sorting.Moved", what, DescribePath(target));
            if (_notices is not null)
            {
                _notices.Show(L.T("Sorting.Title"), message, FAInfoBarSeverity.Success, L.T("Sorting.Undo"), () => _ = UndoAsync(operation.Id));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show(L.T("Sorting.MoveFailed"), exception.Message, FAInfoBarSeverity.Error);
        }
    }

    private bool CanMove() => SelectedFolder is not null && SelectedCount > 0;

    /// <summary>Добавляет склад: выбор папки, затем — в индекс, если её там нет.</summary>
    [RelayCommand]
    private async Task AddInboxAsync()
    {
        if (await PickFolderAsync(L.T("Sorting.PickInbox")) is { } path && !_folders.Inboxes.Any(folder => SamePath(folder.Path, path)))
        {
            await SaveAsync(_folders with { Inboxes = [.. _folders.Inboxes, new SortingFolderInfo(path)] }, path);
        }
    }

    /// <summary>Добавляет рабочую папку.</summary>
    [RelayCommand]
    private async Task AddWorkFolderAsync()
    {
        if (await PickFolderAsync(L.T("Sorting.PickWorkFolder")) is { } path)
        {
            await AddWorkFolderAsync(path);
        }
    }

    /// <summary>Закрепляет текущую папку назначения как рабочую.</summary>
    [RelayCommand]
    private async Task PinDestinationAsync()
    {
        if (SelectedFolder is { IsNew: false } folder)
        {
            await AddWorkFolderAsync(folder.Path);
        }
    }

    /// <summary>Убирает склад из списка; папка и файлы остаются.</summary>
    [RelayCommand]
    private async Task RemoveInboxAsync(InboxFilter? inbox)
    {
        if (inbox?.Folder is { } folder)
        {
            await SaveAsync(_folders with { Inboxes = [.. _folders.Inboxes.Where(candidate => !SamePath(candidate.Path, folder.Path))] }, null);
        }
    }

    /// <summary>Убирает рабочую папку из списка; папка и файлы остаются.</summary>
    [RelayCommand]
    private async Task RemoveWorkFolderAsync(WorkFolderItem? item)
    {
        if (item is not null)
        {
            await SaveAsync(_folders with { WorkFolders = [.. _folders.WorkFolders.Where(candidate => !SamePath(candidate.Path, item.Folder.Path))] }, null);
        }
    }

    /// <summary>Сдвигает рабочую папку на одну клавишу раньше.</summary>
    [RelayCommand]
    private Task MoveWorkFolderUpAsync(WorkFolderItem? item) => ShiftWorkFolderAsync(item, -1);

    /// <summary>Сдвигает рабочую папку на одну клавишу позже.</summary>
    [RelayCommand]
    private Task MoveWorkFolderDownAsync(WorkFolderItem? item) => ShiftWorkFolderAsync(item, 1);

    /// <summary>Перечитывает склады.</summary>
    [RelayCommand]
    private Task Refresh() => PendingLoad = RefreshAsync();

    partial void OnSelectedInboxChanged(InboxFilter? value) => ShowItems();

    partial void OnSelectedWorkFolderChanged(WorkFolderItem? value)
    {
        if (value is not null)
        {
            ShowTree(value.Folder);
        }
    }

    partial void OnFolderQueryChanged(string value) => PendingLoad = SearchFoldersAsync(value.Trim());

    partial void OnTagTextChanged(string value)
    {
        TagSuggestions.Clear();
        var text = value.Trim().TrimStart('#');
        if (text.Length == 0)
        {
            return;
        }

        foreach (var tag in _allTags.Where(tag => tag.Contains(text, StringComparison.OrdinalIgnoreCase) && !Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)).Take(8))
        {
            TagSuggestions.Add(tag);
        }
    }

    /// <inheritdoc />
    protected override Task RelocalizeAsync()
    {
        OnPropertyChanged(nameof(DestinationText));
        OnPropertyChanged(nameof(MoveText));
        return RefreshAsync();
    }

    private static bool SamePath(string left, string right)
        => string.Equals(left.TrimEnd('\\', '/'), right.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static string? FolderNameProblem(string name)
    {
        if (name is "." or ".." || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0)
        {
            return L.T("Sorting.BadFolderName");
        }

        return name.EndsWith('.') || name.EndsWith(' ') ? L.T("Sorting.BadFolderName") : null;
    }

    private string DescribePath(SortFolderNode node)
    {
        var parts = new List<string>();
        for (var current = node; current is not null; current = current.Parent)
        {
            parts.Add(current.Parent is null ? (SelectedWorkFolder?.Folder.Title ?? current.Name) : current.Name);
        }

        parts.Reverse();
        return string.Join(" › ", parts);
    }

    private void ShowWorkFolders()
    {
        var selected = SelectedWorkFolder?.Folder.Path;
        WorkFolders.Clear();
        for (var i = 0; i < _folders.WorkFolders.Count; i++)
        {
            WorkFolders.Add(new WorkFolderItem(i < 9 ? i + 1 : 0, _folders.WorkFolders[i]));
        }

        HasNoWorkFolders = WorkFolders.Count == 0;
        SelectedWorkFolder = selected is null ? null : WorkFolders.FirstOrDefault(item => SamePath(item.Folder.Path, selected));
        if (SelectedWorkFolder is null && Tree.FirstOrDefault() is { } root && !WorkFolders.Any(item => SamePath(item.Folder.Path, root.Path)))
        {
            // Назначение из поиска остаётся, даже если оно не рабочая папка.
            return;
        }

        if (SelectedWorkFolder is null)
        {
            Tree.Clear();
            SelectedFolder = null;
        }
    }

    private void ShowTree(SortingFolderInfo folder)
    {
        var root = new SortFolderNode(folder.Title, folder.Path, null, LoadChildrenAsync);
        Tree.Clear();
        Tree.Add(root);
        root.IsExpanded = true;
        SelectedFolder = root;
    }

    private async Task LoadChildrenAsync(SortFolderNode node)
    {
        try
        {
            var listing = await _browse.ListAsync(node.Path, BrowseSort.Name, descending: false);
            node.SetChildren(listing.Items
                .Where(static entry => entry.Kind == FileKind.Folder)
                .Select(entry => new SortFolderNode(entry.Name, entry.FullPath, node, LoadChildrenAsync)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Папка ещё не в индексе или недоступна: в неё всё равно можно перенести, только подпапок не видно.
            node.SetChildren([]);
        }
    }

    private async Task ReloadChildrenAsync(SortFolderNode node)
    {
        await Task.Delay(TimeSpan.FromSeconds(1));
        var selectedPath = SelectedFolder?.Path;
        await LoadChildrenAsync(node);
        if (selectedPath is not null && node.Children.FirstOrDefault(child => SamePath(child.Path, selectedPath)) is { } same)
        {
            SelectedFolder = same;
        }
    }

    private async Task LoadInboxesAsync()
    {
        var selected = SelectedInbox?.Folder?.Path;
        _all.Clear();
        Inboxes.Clear();
        HasNoInboxes = _folders.Inboxes.Count == 0;
        var all = new InboxFilter(null, L.T("Sorting.AllInboxes"));
        Inboxes.Add(all);
        foreach (var folder in _folders.Inboxes)
        {
            var filter = new InboxFilter(folder, folder.Title);
            Inboxes.Add(filter);
            try
            {
                var listing = await _browse.ListAsync(folder.Path, BrowseSort.Modified, descending: true);
                _all.AddRange(listing.Items.Select(entry => new SortItem(entry, folder)));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                filter.Problem = exception.Message;
            }
        }

        _all.Sort(static (left, right) => right.Entry.Modified.CompareTo(left.Entry.Modified));
        UpdateCounts();
        SelectedInbox = Inboxes.FirstOrDefault(filter => filter.Folder is not null && selected is not null && SamePath(filter.Folder.Path, selected)) ?? all;
        ShowItems();
    }

    private async Task LoadTagsAsync()
    {
        try
        {
            _allTags = [.. (await _library.GetTagsAsync()).Select(static tag => tag.Name)];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Без подсказок теги всё равно вводятся вручную.
            _ = exception;
        }
    }

    private void ShowItems()
    {
        Items.Clear();
        var folder = SelectedInbox?.Folder;
        foreach (var item in _all.Where(item => folder is null || SamePath(item.Inbox.Path, folder.Path)))
        {
            Items.Add(item);
        }

        SetSelection([]);
        EmptyHint = HasNoInboxes
            ? L.T("Sorting.NoInboxesHint")
            : Items.Count == 0 ? (SelectedInbox?.Problem is { } problem ? L.F("Sorting.InboxProblem", problem) : L.T("Sorting.InboxEmpty")) : string.Empty;
    }

    private void UpdateCounts()
    {
        foreach (var filter in Inboxes)
        {
            filter.Count = filter.Folder is null ? _all.Count : _all.Count(item => SamePath(item.Inbox.Path, filter.Folder.Path));
        }

        BadgeValue = _all.Count;
        UpdateItemsText();
    }

    private void UpdateItemsText()
    {
        var count = Formatting.Count(Items.Count, L.T("Common.File.One"), L.T("Common.File.Few"), L.T("Common.File.Many"));
        ItemsText = SelectedCount > 0 ? L.F("Sorting.ItemsSelected", count, SelectedCount) : count;
    }

    private async Task SearchFoldersAsync(string text)
    {
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _folderSearch, cts);
        if (previous is not null)
        {
            await previous.CancelAsync();
            previous.Dispose();
        }

        FolderResults.Clear();
        if (text.Length == 0)
        {
            return;
        }

        try
        {
            var result = await _search.SearchAsync("is:dir " + text, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            foreach (var entry in result.Entries.Where(static entry => entry.Kind == FileKind.Folder && !entry.IsOffline).Take(12))
            {
                FolderResults.Add(new SortingFolderInfo(entry.FullPath, entry.Name));
            }
        }
        catch (OperationCanceledException)
        {
            // Пользователь продолжил ввод.
        }
        catch (Exception exception)
        {
            _ = exception;
        }
    }

    private async Task<string?> PickFolderAsync(string title) => _dialogs is null ? null : await _dialogs.PickFolderAsync(title);

    private async Task AddWorkFolderAsync(string path)
    {
        if (!_folders.WorkFolders.Any(folder => SamePath(folder.Path, path)))
        {
            await SaveAsync(_folders with { WorkFolders = [.. _folders.WorkFolders, new SortingFolderInfo(path)] }, path);
        }

        SelectedWorkFolder = WorkFolders.FirstOrDefault(item => SamePath(item.Folder.Path, path));
    }

    private async Task ShiftWorkFolderAsync(WorkFolderItem? item, int offset)
    {
        if (item is null)
        {
            return;
        }

        var list = _folders.WorkFolders.ToList();
        var index = list.FindIndex(folder => SamePath(folder.Path, item.Folder.Path));
        var target = index + offset;
        if (index < 0 || target < 0 || target >= list.Count)
        {
            return;
        }

        (list[index], list[target]) = (list[target], list[index]);
        await SaveAsync(_folders with { WorkFolders = list }, null);
    }

    private async Task SaveAsync(SortingFolders folders, string? added)
    {
        try
        {
            if (added is not null)
            {
                await EnsureIndexedAsync(added);
            }

            await _settings.SaveAsync(folders);
            _folders = folders;
            ShowWorkFolders();
            await LoadInboxesAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show(L.T("Sorting.Title"), L.F("Sorting.SaveFailed", exception.Message), FAInfoBarSeverity.Error);
        }
    }

    // Поиск идёт только по индексу: папка вне корней добавляется корнем, иначе её файлы не найдутся.
    private async Task EnsureIndexedAsync(string path)
    {
        var roots = await _roots.GetRootsAsync();
        if (roots.Any(root => SamePath(root.Path, path) || path.StartsWith(root.Path.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        await _roots.AddAsync(path);
        _notices?.Show(L.T("Sorting.Title"), L.F("Sorting.AddedToIndex", PathText.FileName(path)), FAInfoBarSeverity.Informational);
    }

    private async Task UndoAsync(Guid operationId)
    {
        try
        {
            await _operations.UndoAsync(operationId);
            await Task.Delay(TimeSpan.FromSeconds(1));
            await LoadInboxesAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show(L.T("Sorting.Undo"), exception.Message, FAInfoBarSeverity.Error);
        }
    }
}

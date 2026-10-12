using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DiskApp.UI.Localization;
using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.Text;

using FluentAvalonia.UI.Controls;

using Symbol = FluentIcons.Common.Symbol;

namespace DiskApp.UI.ViewModels;

/// <summary>Звено адресной строки: корень или папка на пути к текущей.</summary>
/// <param name="Label">Подпись.</param>
/// <param name="Path">Полный путь.</param>
public sealed record BrowseCrumb(string Label, string Path)
{
    /// <inheritdoc />
    /// <remarks>Крошки FluentAvalonia показывают строку элемента.</remarks>
    public override string ToString() => Label;
}

/// <summary>Вариант фильтра по виду файла.</summary>
/// <param name="Label">Подпись.</param>
/// <param name="Kinds">Виды; пусто — все.</param>
public sealed record KindFilterOption(string Label, IReadOnlyList<FileKind> Kinds)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>Вариант сортировки.</summary>
/// <param name="Label">Подпись.</param>
/// <param name="Sort">Порядок.</param>
public sealed record SortOption(string Label, BrowseSort Sort)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>
/// Узел дерева папок: подпапки загружаются при первом раскрытии.
/// </summary>
public sealed partial class FolderNode : ObservableObject
{
    private readonly Func<FolderNode, Task>? _load;
    private bool _loaded;

    /// <summary>
    /// Создаёт узел.
    /// </summary>
    /// <param name="name">Подпись.</param>
    /// <param name="path">Полный путь; пусто у заглушки.</param>
    /// <param name="symbol">Значок.</param>
    /// <param name="load">Загрузка подпапок; <c>null</c> у заглушки.</param>
    public FolderNode(string name, string path, Symbol symbol, Func<FolderNode, Task>? load)
    {
        Name = name;
        Path = path;
        Symbol = symbol;
        _load = load;
        if (load is not null)
        {
            // Заглушка показывает стрелку раскрытия, пока подпапки не загружены.
            Children.Add(new FolderNode("…", string.Empty, Symbol.MoreHorizontal, null));
        }
    }

    /// <summary>Подпись.</summary>
    public string Name { get; }

    /// <summary>Полный путь.</summary>
    public string Path { get; }

    /// <summary>Значок.</summary>
    public Symbol Symbol { get; }

    /// <summary>Заглушка «загружается».</summary>
    public bool IsPlaceholder => _load is null;

    /// <summary>Подпапки.</summary>
    public ObservableCollection<FolderNode> Children { get; } = [];

    /// <summary>Узел раскрыт; при первом раскрытии загружаются подпапки.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>Загружает подпапки (один раз).</summary>
    public Task LoadAsync()
    {
        if (_loaded || _load is null)
        {
            return Task.CompletedTask;
        }

        _loaded = true;
        return _load(this);
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            _ = LoadAsync();
        }
    }
}

/// <summary>
/// Страница «Обзор» (PLAN.md, этап 8.3): дерево корней и папок, содержимое папки списком или плиткой, адресная
/// строка и крошки, история «назад — вперёд», сортировка, фильтры по имени и виду, выбор нескольких записей,
/// сведения о выбранной и действия над выбранными.
/// </summary>
public sealed partial class BrowseViewModel : PageViewModel
{
    private readonly IBrowseService _browse;
    private readonly IRootsService _roots;
    private readonly ISearchService _search;
    private readonly IFileActions _actions;
    private readonly IDialogService? _dialogs;
    private readonly IInfoBarService? _notices;
    private readonly IOperationsCoordinator? _operations;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();
    private readonly Dictionary<long, RootInfo> _rootsById = [];
    private List<FileEntry> _listing = [];
    private IReadOnlyList<SearchHit> _selection = [];
    private FolderListing? _current;
    private readonly bool _ready;

    /// <summary>
    /// Создаёт страницу приложения.
    /// </summary>
    public BrowseViewModel(
        IBrowseService browse,
        IRootsService roots,
        ISearchService search,
        IFileActions actions,
        IDialogService dialogs,
        IInfoBarService notices,
        IOperationsCoordinator operations,
        ITagEditor? tagEditor = null)
        : this(browse, roots, search, actions, dialogs, notices, operations, static () => DateTimeOffset.Now)
    {
        TagEditor = tagEditor;
    }

    /// <summary>
    /// Создаёт страницу без очереди операций (превью, тесты обзора).
    /// </summary>
    public BrowseViewModel(IBrowseService browse, IRootsService roots, ISearchService search, IFileActions actions, IDialogService? dialogs, IInfoBarService? notices, Func<DateTimeOffset> clock)
        : this(browse, roots, search, actions, dialogs, notices, null, clock)
    {
    }

    /// <summary>
    /// Создаёт страницу с заданными сервисами и часами (тесты).
    /// </summary>
    public BrowseViewModel(IBrowseService browse, IRootsService roots, ISearchService search, IFileActions actions, IDialogService? dialogs, IInfoBarService? notices, IOperationsCoordinator? operations, Func<DateTimeOffset> clock)
        : base(PageKeys.Browse, "Browse.Browse", FASymbol.Folder)
    {
        ArgumentNullException.ThrowIfNull(browse);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(clock);

        _browse = browse;
        _roots = roots;
        _search = search;
        _actions = actions;
        _dialogs = dialogs;
        _notices = notices;
        _operations = operations;
        _clock = clock;
        if (operations is not null)
        {
            operations.OperationFinished += OnOperationFinished;
        }

        SelectedKind = KindOptions[0];
        SelectedSort = SortOptions[0];
        _ready = true;
        Initialization = LoadRootsAsync();
    }

    /// <summary>Первая загрузка: корни и содержимое первого корня.</summary>
    public Task Initialization { get; private set; }

    /// <summary>Варианты фильтра по виду.</summary>
    public IReadOnlyList<KindFilterOption> KindOptions { get; } =
    [
        new(L.T("Browse.All"), []),
        new(L.T("Browse.Folders"), [FileKind.Folder]),
        new(L.T("Browse.Documents"), [FileKind.Document, FileKind.Text, FileKind.Pdf]),
        new(L.T("Browse.Spreadsheets"), [FileKind.Spreadsheet]),
        new(L.T("Browse.Images"), [FileKind.Image]),
        new(L.T("Browse.Videos"), [FileKind.Video]),
        new(L.T("Browse.Audio"), [FileKind.Audio]),
        new(L.T("Browse.Archives"), [FileKind.Archive]),
        new(L.T("Browse.Code"), [FileKind.Code]),
    ];

    /// <summary>Варианты сортировки.</summary>
    public IReadOnlyList<SortOption> SortOptions { get; } =
    [
        new(L.T("Browse.Name"), BrowseSort.Name),
        new(L.T("Browse.DateModified"), BrowseSort.Modified),
        new(L.T("Browse.Size"), BrowseSort.Size),
        new(L.T("Browse.Type"), BrowseSort.Kind),
    ];

    /// <summary>Дерево: корни и раскрытые папки.</summary>
    public ObservableCollection<FolderNode> Tree { get; } = [];

    /// <summary>Крошки от корня до текущей папки.</summary>
    public ObservableCollection<BrowseCrumb> Crumbs { get; } = [];

    /// <summary>Содержимое текущей папки после фильтров.</summary>
    public ObservableCollection<SearchHit> Items { get; } = [];

    /// <summary>Текущая папка.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoUp), nameof(CanEditAutoTags), nameof(AutoTagsMenuText))]
    public partial string? CurrentPath { get; set; }

    /// <summary>Текст адресной строки.</summary>
    [ObservableProperty]
    public partial string AddressText { get; set; } = string.Empty;

    /// <summary>Вид «плитка» вместо списка.</summary>
    [ObservableProperty]
    public partial bool IsTiles { get; set; }

    /// <summary>Сортировка.</summary>
    [ObservableProperty]
    public partial SortOption SelectedSort { get; set; }

    /// <summary>По убыванию.</summary>
    [ObservableProperty]
    public partial bool Descending { get; set; }

    /// <summary>Быстрый фильтр по имени в текущей папке.</summary>
    [ObservableProperty]
    public partial string Filter { get; set; } = string.Empty;

    /// <summary>Фильтр по виду.</summary>
    [ObservableProperty]
    public partial KindFilterOption SelectedKind { get; set; }

    /// <summary>Идёт загрузка папки.</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>Ошибка загрузки папки; пусто — всё хорошо.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorText { get; set; } = string.Empty;

    /// <summary>Строка состояния: «24 элемента · выбрано 3, 12 МБ».</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    /// <summary>Сведения о выбранной записи (одной).</summary>
    [ObservableProperty]
    public partial FileDetails? SelectedDetails { get; set; }

    /// <summary>Есть ли ошибка.</summary>
    public bool HasError => ErrorText.Length > 0;

    /// <summary>
    /// Подсказка вместо пустого списка (PLAN.md, этап 9.3): корней нет, папка пуста или фильтр ничего не оставил; пусто —
    /// в списке есть записи.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmptyHint))]
    public partial string EmptyHint { get; set; } = string.Empty;

    /// <summary>Показать подсказку вместо пустого списка.</summary>
    public bool HasEmptyHint => EmptyHint.Length > 0;

    /// <summary>Можно вернуться назад.</summary>
    public bool CanGoBack => _back.Count > 0;

    /// <summary>Можно пойти вперёд.</summary>
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>Можно подняться на уровень выше: текущая папка не корень.</summary>
    public bool CanGoUp => _current?.Folder is not null;

    /// <summary>Выбранные записи.</summary>
    public IReadOnlyList<SearchHit> Selection => _selection;

    /// <summary>
    /// Переходит в папку внутри корней.
    /// </summary>
    /// <param name="path">Полный путь.</param>
    /// <param name="pushHistory">Запомнить текущую папку для «назад».</param>
    public async Task NavigateAsync(string path, bool pushHistory = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        IsLoading = true;
        try
        {
            var listing = await _browse.ListAsync(path.Trim(), SelectedSort.Sort, Descending);
            if (pushHistory && CurrentPath is { } previous && !string.Equals(previous, listing.Path, StringComparison.OrdinalIgnoreCase))
            {
                _back.Push(previous);
                _forward.Clear();
            }

            ShowListing(listing);
            ErrorText = string.Empty;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorText = L.F("Browse.CouldNotOpen", path, exception.Message);
            AddressText = CurrentPath ?? string.Empty;
        }
        finally
        {
            IsLoading = false;
            NotifyHistory();
        }
    }

    /// <summary>
    /// Запоминает выбор в списке: сведения, строка состояния.
    /// </summary>
    public void UpdateSelection(IReadOnlyList<SearchHit> selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        _selection = selection;
        SelectedDetails = selection.Count == 1 ? new FileDetails(selection[0].Entry) : null;
        UpdateStatus();
        OnPropertyChanged(nameof(Selection));
        OnPropertyChanged(nameof(CanEditAutoTags));
        OnPropertyChanged(nameof(AutoTagsMenuText));
    }

    /// <summary>Перейти по адресной строке.</summary>
    [RelayCommand]
    private Task GoToAddressAsync() => AddressText.Trim().Length == 0 ? Task.CompletedTask : NavigateAsync(AddressText);

    /// <summary>Назад по истории.</summary>
    [RelayCommand]
    private async Task GoBackAsync()
    {
        if (_back.Count == 0)
        {
            return;
        }

        var target = _back.Pop();
        if (CurrentPath is { } current)
        {
            _forward.Push(current);
        }

        await NavigateAsync(target, pushHistory: false);
    }

    /// <summary>Вперёд по истории.</summary>
    [RelayCommand]
    private async Task GoForwardAsync()
    {
        if (_forward.Count == 0)
        {
            return;
        }

        var target = _forward.Pop();
        if (CurrentPath is { } current)
        {
            _back.Push(current);
        }

        await NavigateAsync(target, pushHistory: false);
    }

    /// <summary>На уровень выше.</summary>
    [RelayCommand]
    private Task GoUpAsync()
        => CanGoUp && CurrentPath is { } current && PathText.DirectoryName(current) is { Length: > 0 } parent
            ? NavigateAsync(parent)
            : Task.CompletedTask;

    /// <inheritdoc />
    protected override Task RelocalizeAsync() => RefreshAsync();

    /// <summary>Перечитать папку.</summary>
    [RelayCommand]
    private Task RefreshAsync() => CurrentPath is { } current ? NavigateAsync(current, pushHistory: false) : LoadRootsAsync();

    /// <summary>Перейти по крошке.</summary>
    [RelayCommand]
    private Task NavigateCrumbAsync(BrowseCrumb crumb)
    {
        ArgumentNullException.ThrowIfNull(crumb);
        return NavigateAsync(crumb.Path);
    }

    /// <summary>Перейти в папку из дерева.</summary>
    [RelayCommand]
    private Task NavigateNodeAsync(FolderNode? node) => node is { IsPlaceholder: false } ? NavigateAsync(node.Path) : Task.CompletedTask;

    /// <summary>Открыть запись: папку — внутри обзора, файл — приложением по умолчанию.</summary>
    [RelayCommand]
    private async Task OpenItemAsync(SearchHit? hit)
    {
        if (hit is null)
        {
            return;
        }

        if (hit.Entry.Kind == FileKind.Folder)
        {
            await NavigateAsync(hit.Entry.FullPath);
            return;
        }

        await TryAsync(() => _search.OpenAsync(hit.Entry, null, CancellationToken.None), L.F("Browse.CouldNotOpen2", hit.Entry.Name));
    }

    /// <summary>Переключить список и плитку.</summary>
    [RelayCommand]
    private void ToggleView() => IsTiles = !IsTiles;

    /// <summary>Показать выбранное в Проводнике.</summary>
    [RelayCommand]
    private Task RevealAsync() => Single() is { } entry ? TryAsync(() => _actions.RevealAsync(entry), L.T("Browse.CouldNotShowInFolder")) : Task.CompletedTask;

    /// <summary>Свойства выбранного.</summary>
    [RelayCommand]
    private Task PropertiesAsync() => Single() is { } entry ? TryAsync(() => _actions.ShowPropertiesAsync(entry), L.T("Browse.CouldNotOpenProperties")) : Task.CompletedTask;

    /// <summary>Скопировать выбранные файлы: в буфер «Обзора» (для вставки) и в системный буфер обмена (для Проводника).</summary>
    [RelayCommand]
    private async Task CopyFilesAsync()
    {
        var entries = Selected();
        if (entries.Count > 0)
        {
            _clipboard = [.. entries.Select(static entry => entry.FullPath)];
            _clipboardCut = false;
            OnPropertyChanged(nameof(HasClipboard));
        }

        if (entries.Count > 0 && await TryAsync(() => _actions.CopyFilesAsync(entries), L.T("Browse.CouldNotCopy")))
        {
            _notices?.Show(L.T("Browse.Copied"), Formatting.Count(entries.Count, L.T("Common.Item.One"), L.T("Common.Item.Few"), L.T("Common.Item.Many")) + L.T("Browse.PasteInFileExplorer"), FAInfoBarSeverity.Success);
        }
    }

    /// <summary>Скопировать пути выбранных.</summary>
    [RelayCommand]
    private async Task CopyPathAsync()
    {
        var entries = Selected();
        if (entries.Count > 0 && await TryAsync(() => _actions.CopyPathsAsync(entries), L.T("Browse.CouldNotCopyThePath")))
        {
            _notices?.Show(L.T("Browse.PathCopied"), entries.Count == 1 ? entries[0].FullPath : Formatting.Count(entries.Count, L.T("Common.Path.One"), L.T("Common.Path.Few"), L.T("Common.Path.Many")), FAInfoBarSeverity.Success);
        }
    }

    /// <summary>Переименовать выбранное (одно).</summary>
    [RelayCommand]
    private async Task RenameAsync()
    {
        if (Single() is not { } entry || _dialogs is null)
        {
            return;
        }

        var name = await _dialogs.PromptAsync(L.T("Common.Rename"), L.F("Browse.NewNameFor", entry.Name), entry.Name, L.T("Common.Rename"), PathText.StemLength(entry.Name));
        if (name is null || name.Trim() == entry.Name)
        {
            return;
        }

        if (await TryAsync(() => _actions.RenameAsync(entry, name), L.T("Browse.CouldNotRename")))
        {
            await RefreshAsync();
        }
    }

    /// <summary>Переместить выбранное в корзину после подтверждения.</summary>
    [RelayCommand]
    private async Task DeleteAsync()
    {
        var entries = Selected();
        if (entries.Count == 0 || _dialogs is null)
        {
            return;
        }

        var what = entries.Count == 1 ? $"«{entries[0].Name}»" : Formatting.Count(entries.Count, L.T("Common.Item.One"), L.T("Common.Item.Few"), L.T("Common.Item.Many"));
        if (!await _dialogs.ConfirmAsync(L.T("Common.ToRecycleBin"), L.F("Browse.MoveToTheRecycleBinYou", what), L.T("Common.ToRecycleBin")))
        {
            return;
        }

        if (await TryAsync(() => _actions.MoveToRecycleBinAsync(entries), L.T("Browse.CouldNotMoveToTheRecycle")))
        {
            _notices?.Show(L.T("Browse.MovedToTheRecycleBin"), what, FAInfoBarSeverity.Success);
            await RefreshAsync();
        }
    }

    /// <summary>Диалог тегов (PLAN.md, этап 8.7); без него команда «Теги…» скрыта.</summary>
    public ITagEditor? TagEditor { get; init; }

    /// <summary>Теги можно менять.</summary>
    public bool CanEditTags => TagEditor is not null;

    /// <summary>«Авто-теги папки…» доступны: выбрана одна папка или ничего — тогда для открытой.</summary>
    public bool CanEditAutoTags => TagEditor is not null && AutoTagsTarget is not null;

    /// <summary>Пункт меню с именем папки, к которой он относится: выбранной подпапки или открытой — чтобы не гадать.</summary>
    public string AutoTagsMenuText => AutoTagsTarget is { } folder
        ? L.F("Browse.AutoTagsOfFolder", PathText.FileName(folder))
        : L.T("Browse.AutoTagsEllipsis");

    /// <summary>Папка для «Авто-теги папки…» из меню списка: выбранная папка, а без выбора — открытая.</summary>
    private string? AutoTagsTarget => _selection.Count == 1
        ? _selection[0].Entry.Kind == FileKind.Folder ? _selection[0].Entry.FullPath : null
        : _selection.Count == 0 ? CurrentPath : null;

    /// <summary>
    /// Закрепить выбранное или открепить (<c>Ctrl+D</c>, PLAN.md, этап 8.7): если среди выбранного есть незакреплённое —
    /// закрепляется всё, иначе всё открепляется.
    /// </summary>
    [RelayCommand]
    private async Task TogglePinAsync()
    {
        var entries = Selected();
        if (entries.Count == 0)
        {
            return;
        }

        var favorite = entries.Any(static entry => !entry.IsFavorite);
        var changed = entries.Where(entry => entry.IsFavorite != favorite).ToArray();
        if (await TryAsync(async () =>
        {
            foreach (var entry in changed)
            {
                await _actions.SetFavoriteAsync(entry, favorite);
            }
        }, L.T("Browse.CouldNotChangePinning")))
        {
            var what = changed.Length == 1 ? $"«{changed[0].Name}»" : Formatting.Count(changed.Length, L.T("Common.Item.One"), L.T("Common.Item.Few"), L.T("Common.Item.Many"));
            _notices?.Show(favorite ? L.T("Browse.Pinned") : L.T("Browse.Unpinned"), what, FAInfoBarSeverity.Success);
            await RefreshAsync();
        }
    }

    /// <summary>Теги выбранного (<c>Ctrl+T</c>).</summary>
    [RelayCommand]
    private async Task EditTagsAsync()
    {
        var entries = Selected();
        if (entries.Count == 0 || TagEditor is null)
        {
            return;
        }

        IReadOnlyList<FileEntry>? updated = null;
        if (await TryAsync(async () => updated = await TagEditor.EditAsync(entries), L.T("Browse.CouldNotChangeTags")) && updated is { Count: > 0 })
        {
            _notices?.Show(L.T("Browse.TagsSaved"), updated.Count == 1 ? $"«{updated[0].Name}»" : Formatting.Count(updated.Count, L.T("Common.File.One"), L.T("Common.File.Few"), L.T("Common.File.Many")), FAInfoBarSeverity.Success);
            await RefreshAsync();
        }
    }

    /// <summary>
    /// «Авто-теги папки…» (PLAN.md, этап 19): теги, которые получает всё внутри выбранной папки, а если ничего не выбрано —
    /// внутри открытой.
    /// </summary>
    [RelayCommand]
    private Task EditAutoTagsAsync() => AutoTagsTarget is { } folder ? EditAutoTagsOfAsync(folder) : Task.CompletedTask;

    /// <summary>
    /// «Авто-теги папки…» из меню дерева: так задаются авто-теги корня — например, рабочей папки, добавленной корнем, —
    /// которого нет в списке ни одной папки.
    /// </summary>
    [RelayCommand]
    private Task EditFolderAutoTagsAsync(FolderNode? node)
        => node is { IsPlaceholder: false, Path.Length: > 0 } ? EditAutoTagsOfAsync(node.Path) : Task.CompletedTask;

    private async Task EditAutoTagsOfAsync(string folder)
    {
        if (TagEditor is null)
        {
            return;
        }

        var changed = false;
        if (await TryAsync(async () => changed = await TagEditor.EditAutoTagsAsync(folder), L.T("Browse.CouldNotChangeTags")) && changed)
        {
            _notices?.Show(L.T("Browse.AutoTagsSaved"), $"«{PathText.FileName(folder)}»", FAInfoBarSeverity.Success);
            await RefreshAsync();
        }
    }

    partial void OnSelectedSortChanged(SortOption value) => ReloadForSort();

    partial void OnDescendingChanged(bool value) => ReloadForSort();

    partial void OnFilterChanged(string value) => ApplyView();

    partial void OnSelectedKindChanged(KindFilterOption value) => ApplyView();

    private void ReloadForSort()
    {
        if (!_ready)
        {
            return;
        }

        if (CurrentPath is { } current && !IsLoading)
        {
            _ = NavigateAsync(current, pushHistory: false);
        }
    }

    private async Task LoadRootsAsync()
    {
        IReadOnlyList<RootInfo> roots;
        try
        {
            roots = await _roots.GetRootsAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ErrorText = L.T("Browse.CouldNotGetTheRoots") + exception.Message;
            return;
        }

        Tree.Clear();
        _rootsById.Clear();
        foreach (var root in roots)
        {
            _rootsById[root.Id] = root;
            Tree.Add(new FolderNode(root.Label, root.Path, root.State == RootState.Offline ? Symbol.PlugDisconnected : Symbol.HardDrive, LoadChildrenAsync));
        }

        if (CurrentPath is null && roots.FirstOrDefault(static root => root.State != RootState.Offline) is { } first)
        {
            await NavigateAsync(first.Path, pushHistory: false);
        }
        else if (roots.Count == 0)
        {
            StatusText = L.T("Browse.NoRootsYetAddAFolder");
            EmptyHint = L.T("Browse.EmptyNoRoots");
        }
    }

    private async Task LoadChildrenAsync(FolderNode node)
    {
        try
        {
            var listing = await _browse.ListAsync(node.Path, BrowseSort.Name, descending: false);
            node.Children.Clear();
            foreach (var folder in listing.Items.Where(static entry => entry.Kind == FileKind.Folder))
            {
                node.Children.Add(new FolderNode(folder.Name, folder.FullPath, Symbol.Folder, LoadChildrenAsync));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            node.Children.Clear();
            _notices?.Show(L.F("Browse.CouldNotOpen2", node.Name), exception.Message, FAInfoBarSeverity.Error);
        }
    }

    private void ShowListing(FolderListing listing)
    {
        _current = listing;
        CurrentPath = listing.Path;
        AddressText = listing.Path;
        _listing = [.. listing.Items];
        BuildCrumbs(listing);
        ApplyView();
        OnPropertyChanged(nameof(CanGoUp));
    }

    private void BuildCrumbs(FolderListing listing)
    {
        Crumbs.Clear();
        if (!_rootsById.TryGetValue(listing.RootId, out var root))
        {
            Crumbs.Add(new BrowseCrumb(PathText.FileName(listing.Path), listing.Path));
            return;
        }

        Crumbs.Add(new BrowseCrumb(root.Label, root.Path));
        var rootPath = root.Path.TrimEnd('\\', '/');
        if (listing.Path.Length <= rootPath.Length)
        {
            return;
        }

        var separator = listing.Path.Contains('\\', StringComparison.Ordinal) ? '\\' : '/';
        var path = rootPath;
        foreach (var segment in listing.Path[rootPath.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            path += separator + segment;
            Crumbs.Add(new BrowseCrumb(segment, path));
        }
    }

    private void ApplyView()
    {
        if (!_ready)
        {
            return;
        }

        var filter = Filter.Trim();
        IEnumerable<FileEntry> visible = _listing
            .Where(entry => filter.Length == 0 || entry.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            .Where(entry => SelectedKind.Kinds.Count == 0 || SelectedKind.Kinds.Contains(entry.Kind));
        if (SelectedSort.Sort == BrowseSort.Kind)
        {
            visible = Descending
                ? visible.OrderByDescending(static entry => entry.Kind).ThenBy(static entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                : visible.OrderBy(static entry => entry.Kind).ThenBy(static entry => entry.Name, StringComparer.CurrentCultureIgnoreCase);
        }

        var now = _clock();
        Items.Clear();
        foreach (var entry in visible)
        {
            Items.Add(new SearchHit(entry, filter, 0, now));
        }

        _selection = [];
        SelectedDetails = null;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var text = Formatting.Count(Items.Count, L.T("Common.Item.One"), L.T("Common.Item.Few"), L.T("Common.Item.Many"));
        if (Items.Count != _listing.Count)
        {
            text += L.F("Browse.Of", _listing.Count);
        }

        if (_current?.Truncated == true)
        {
            text += L.T("Browse.ShowingTheFirst");
        }

        if (_selection.Count > 0)
        {
            var size = _selection.Where(static hit => hit.Entry.Kind != FileKind.Folder).Sum(static hit => hit.Entry.Size);
            text += L.F("Browse.Selected", _selection.Count) + (size > 0 ? ", " + Formatting.Size(size) : string.Empty);
        }

        StatusText = text;
        if (CurrentPath is not null)
        {
            EmptyHint = Items.Count > 0 || HasError ? string.Empty
                : _listing.Count > 0 ? L.T("Browse.EmptyFilter")
                : L.T("Browse.EmptyFolder");
        }
    }

    private void NotifyHistory()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanGoUp));
    }

    private FileEntry? Single() => _selection.Count == 1 ? _selection[0].Entry : null;

    private List<FileEntry> Selected() => [.. _selection.Select(static hit => hit.Entry)];

    private async Task<bool> TryAsync(Func<Task> action, string failure)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show(failure, exception.Message, FAInfoBarSeverity.Error);
            return false;
        }
    }
}

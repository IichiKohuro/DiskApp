using System.Collections.ObjectModel;
using System.Globalization;

using Avalonia.Controls;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DiskApp.UI.Localization;
using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.Text;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.ViewModels;

/// <summary>Папка-накопитель в фильтре над списком файлов; «Все накопители» — без папки.</summary>
public sealed partial class InboxFilter : ObservableObject
{
    /// <summary>Создаёт фильтр.</summary>
    public InboxFilter(SortingFolderInfo? folder, string title)
    {
        Folder = folder;
        Title = title;
    }

    /// <summary>Папка-накопитель; <c>null</c> — все накопители.</summary>
    public SortingFolderInfo? Folder { get; }

    /// <summary>Подпись.</summary>
    public string Title { get; }

    /// <summary>Сколько файлов ждёт разбора.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    public partial int Count { get; set; }

    /// <summary>Накопитель не прочитан: вне индекса или ещё сканируется.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    public partial string? Problem { get; set; }

    /// <summary>Основная папка-накопитель: сюда падают файлы из окна Drag&amp;Drop (этап 18.2).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMakeMain))]
    public partial bool IsMain { get; set; }

    /// <summary>Чип настоящего накопителя, а не «Все накопители»: у него есть меню.</summary>
    public bool IsFolder => Folder is not null;

    /// <summary>Накопитель можно сделать основным.</summary>
    public bool CanMakeMain => Folder is not null && !IsMain;

    /// <summary>Подпись с числом файлов.</summary>
    public string Text => Problem is null ? $"{Title} · {Count}" : $"{Title} · !";

    /// <inheritdoc />
    public override string ToString() => Text;
}

/// <summary>Файл или папка из накопителя.</summary>
/// <param name="Entry">Запись индекса.</param>
/// <param name="Inbox">Накопитель, где лежит.</param>
/// <param name="State">Состояние в очереди (этап 17.1): новый, отложен или оставлен здесь.</param>
public sealed record SortItem(FileEntry Entry, SortingFolderInfo Inbox, InboxItemState State = InboxItemState.New)
{
    /// <summary>Отложен (этап 17.1): в конце списка, не считается в бейдже.</summary>
    public bool IsPostponed => State == InboxItemState.Postponed;

    /// <summary>«Оставить здесь»: виден, только если показаны оставленные.</summary>
    public bool IsKept => State == InboxItemState.Kept;

    /// <summary>Файл не новый: строку можно вернуть в новые.</summary>
    public bool HasState => State != InboxItemState.New;

    /// <summary>Пометка состояния под именем; у нового — пусто.</summary>
    public string StateText => State switch
    {
        InboxItemState.Postponed => L.T("Sorting.StatePostponed"),
        InboxItemState.Kept => L.T("Sorting.StateKept"),
        _ => string.Empty,
    };

    /// <summary>
    /// Та же строка при обновлении очереди: тот же путь, состояние и время изменения — тогда выделение в списке сохраняется.
    /// </summary>
    public string Key => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Entry.FullPath}|{(int)State}|{Entry.Modified.UtcTicks}");

    /// <summary>Когда изменён — для строки списка.</summary>
    public string ModifiedText => Formatting.RelativeTime(Entry.Modified, DateTimeOffset.Now);

    /// <summary>Значок по виду файла, пока нет иконки Windows.</summary>
    public FluentIcons.Common.Symbol Symbol => FileKindSymbols.For(Entry.Kind);

    /// <summary>Размер — для строки списка; у папки пусто.</summary>
    public string SizeText => Entry.Kind == FileKind.Folder ? string.Empty : Formatting.Size(Entry.Size);

    /// <inheritdoc />
    public override string ToString() => Entry.Name;
}

/// <summary>Готовый шаблон имени при переносе.</summary>
/// <param name="Title">Подпись в списке.</param>
/// <param name="Template">Шаблон; пусто — имя не меняется.</param>
public sealed record NamePreset(string Title, string Template)
{
    /// <inheritdoc />
    public override string ToString() => Title;
}

/// <summary>Рабочая папка с клавишей 1–9.</summary>
/// <param name="Number">Номер клавиши; 0 — без клавиши (десятая и дальше).</param>
/// <param name="Folder">Папка.</param>
/// <param name="IsMissing">Папки нет в индексе: переименована, перенесена или удалена (этап 17.2).</param>
public sealed record WorkFolderItem(int Number, SortingFolderInfo Folder, bool IsMissing = false)
{
    /// <summary>Подпись для чтения с экрана и списков: «2 417».</summary>
    public string Text => Number > 0 ? $"{Number} {Folder.Title}" : Folder.Title;

    /// <summary>Клавиша папки — на чипе отдельной плашкой, чтобы «1» не сливалась с подписью «10S-417».</summary>
    public string NumberText => Number > 0 ? Number.ToString(CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>У папки есть клавиша 1–9.</summary>
    public bool HasNumber => Number > 0;

    /// <summary>Подсказка: путь, а если папки нет в индексе — что с ней делать (этап 17.2).</summary>
    public string Tip => IsMissing ? L.F("Sorting.WorkFolderMissing", Folder.Path) : Folder.Path;

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
/// Страница «Разбор» (PLAN.md, этап 17): файлы из накопителей («Загрузки», полученные в мессенджере) раскладываются по
/// рабочим папкам — в подпапку, при необходимости новую, с тегами. Перенос — операция общей очереди, отменяется из
/// уведомления. Папки-накопители и рабочие папки настраиваются здесь же; папка вне индекса добавляется в него, чтобы поиск
/// находил и накопители, и рабочие папки.
/// </summary>
public sealed partial class SortingViewModel : PageViewModel, IPageLifecycle
{
    /// <summary>Ширина панели рабочих папок по умолчанию.</summary>
    public const double DefaultDestinationWidth = 380;

    /// <summary>Уже нельзя: не помещаются чипы и дерево.</summary>
    public const double MinDestinationWidth = 280;

    /// <summary>Шире нельзя: файлам накопителя тоже нужно место.</summary>
    public const double MaxDestinationWidth = 900;

    private const string DestinationsLayoutKey = "sorting.destinations";

    private readonly IFileActions? _actions;
    private readonly ILayoutStore? _layout;
    private readonly ISortingSettings _settings;
    private readonly IBrowseService _browse;
    private readonly IOperationsService _operations;
    private readonly ISearchService _search;
    private readonly ILibraryService _library;
    private readonly IRootsService _roots;
    private readonly IDialogService? _dialogs;
    private readonly IInfoBarService? _notices;
    private readonly IInboxService _inbox;
    private readonly IDispatcher? _dispatcher;
    private CancellationTokenSource? _inboxReload;
    private CancellationTokenSource? _readyTimer;
    private Task _inboxLoad = Task.CompletedTask;
    private readonly List<SortItem> _all = [];
    private IReadOnlyList<SortItem> _selection = [];
    private IReadOnlyList<string> _allTags = [];
    private IReadOnlyList<TagInfo> _tagInfos = [];
    private IReadOnlyList<FolderTagsInfo> _autoTags = [];
    private SortingFolders _folders = SortingFolders.Empty;
    private HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _clock;
    private CancellationTokenSource? _folderSearch;
    private bool _nameTemplateLoaded;

    /// <summary>
    /// Создаёт страницу.
    /// </summary>
    /// <param name="settings">Накопители и рабочие папки.</param>
    /// <param name="browse">Листинг папок: дерево назначения.</param>
    /// <param name="operations">Очередь операций: перенос.</param>
    /// <param name="search">Поиск папок.</param>
    /// <param name="library">Теги и авто-теги.</param>
    /// <param name="roots">Корни: добавить папку вне индекса.</param>
    /// <param name="dialogs">Диалоги.</param>
    /// <param name="notices">Уведомления.</param>
    /// <param name="clock">Часы.</param>
    /// <param name="inbox">Очередь накопителей (этап 17.1); <c>null</c> — по листингу папок (превью, тесты).</param>
    /// <param name="dispatcher">UI-поток для событий очереди; <c>null</c> — вызывать сразу.</param>
    /// <param name="actions">Действия над файлом: «Показать в папке»; <c>null</c> — пункта нет.</param>
    /// <param name="layout">Ширина панели рабочих папок между запусками; <c>null</c> — не запоминается.</param>
    public SortingViewModel(
        ISortingSettings settings,
        IBrowseService browse,
        IOperationsService operations,
        ISearchService search,
        ILibraryService library,
        IRootsService roots,
        IDialogService? dialogs = null,
        IInfoBarService? notices = null,
        Func<DateTimeOffset>? clock = null,
        IInboxService? inbox = null,
        IDispatcher? dispatcher = null,
        IFileActions? actions = null,
        ILayoutStore? layout = null)
        : base(PageKeys.Inbox, "Sorting.Title", FASymbol.Download)
    {
        _clock = clock ?? (static () => DateTimeOffset.Now);
        _actions = actions;

        // Ширина из прошлого запуска ставится до подписки на изменения: в файл попадает только то, что меняли.
        DestinationColumnWidth = new GridLength(Math.Clamp(layout?.GetWidth(DestinationsLayoutKey) ?? DefaultDestinationWidth, MinDestinationWidth, MaxDestinationWidth));
        _layout = layout;
        Tags.CollectionChanged += (_, _) =>
        {
            UpdateNamePreview();
            OnPropertyChanged(nameof(TagsButtonText));
            OnPropertyChanged(nameof(HasTags));
            ClearTagsCommand.NotifyCanExecuteChanged();
        };
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _browse = browse ?? throw new ArgumentNullException(nameof(browse));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _dialogs = dialogs;
        _notices = notices;
        _inbox = inbox ?? new BrowseInboxService(browse, settings, _clock);
        _dispatcher = dispatcher;
        _inbox.Changed += OnInboxChanged;
    }

    /// <summary>Накопители с числом файлов и «Все накопители».</summary>
    public ObservableCollection<InboxFilter> Inboxes { get; } = [];

    /// <summary>Файлы выбранного накопителя, новые сверху.</summary>
    public ObservableCollection<SortItem> Items { get; } = [];

    /// <summary>Рабочие папки в порядке клавиш.</summary>
    public ObservableCollection<WorkFolderItem> WorkFolders { get; } = [];

    /// <summary>Найденные папки по <see cref="FolderQuery"/>.</summary>
    public ObservableCollection<SortingFolderInfo> FolderResults { get; } = [];

    /// <summary>Дерево назначения: выбранная рабочая папка и её подпапки.</summary>
    public ObservableCollection<SortFolderNode> Tree { get; } = [];

    /// <summary>Теги для переносимого.</summary>
    public ObservableCollection<string> Tags { get; } = [];

    /// <summary>Готовые шаблоны имени: как есть, дата в начале, теги в конце…</summary>
    [ObservableProperty]
    public partial IReadOnlyList<NamePreset> NamePresets { get; set; } = BuildNamePresets();

    /// <summary>Выбор в списке готовых шаблонов: подставляет шаблон, сам список возвращается к подсказке.</summary>
    [ObservableProperty]
    public partial NamePreset? SelectedNamePreset { get; set; }

    /// <summary>
    /// Шаблон имени при переносе: своё имя или подстановки <c>{имя}</c>, <c>{дата}</c>, <c>{время}</c>, <c>{теги}</c>,
    /// <c>{папка}</c>, <c>{№}</c>; пусто — имя не меняется.
    /// </summary>
    [ObservableProperty]
    public partial string NameTemplate { get; set; } = string.Empty;

    /// <summary>Как будет называться первый выбранный файл; пусто — шаблона нет или ничего не выбрано.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNamePreview))]
    public partial string NamePreview { get; set; } = string.Empty;

    /// <summary>Предпросмотр имени виден.</summary>
    public bool HasNamePreview => NamePreview.Length > 0;

    /// <summary>Подходящие теги по <see cref="TagText"/>.</summary>
    public ObservableCollection<string> TagSuggestions { get; } = [];

    /// <summary>Выбранный накопитель; «Все накопители» — первый.</summary>
    [ObservableProperty]
    public partial InboxFilter? SelectedInbox { get; set; }

    /// <summary>Выбранная рабочая папка.</summary>
    [ObservableProperty]
    public partial WorkFolderItem? SelectedWorkFolder { get; set; }

    /// <summary>Папка назначения в дереве.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DestinationText), nameof(DestinationAutoTagsText), nameof(HasDestinationAutoTags), nameof(HasDestination))]
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

    /// <summary>Накопителей ещё нет: показывается приглашение добавить.</summary>
    [ObservableProperty]
    public partial bool HasNoInboxes { get; set; }

    /// <summary>Рабочих папок ещё нет.</summary>
    [ObservableProperty]
    public partial bool HasNoWorkFolders { get; set; }

    /// <summary>Идёт чтение накопителей.</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>Подсказка видна.</summary>
    public bool HasEmptyHint => EmptyHint.Length > 0;

    /// <summary>Сколько файлов ещё дописываются (этап 17.1): не прошло «время успокоения», к разбору они не предлагаются.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPending), nameof(PendingText))]
    public partial int PendingCount { get; set; }

    /// <summary>В списке что-то выбрано: «Отложить» и «Оставить здесь» доступны.</summary>
    public bool HasSelection => _selection.Count > 0;

    /// <summary>Среди выбранного есть отложенные или оставленные — их можно вернуть в новые.</summary>
    public bool SelectionHasState => _selection.Any(static item => item.HasState);

    /// <summary>Есть дописываемые файлы.</summary>
    public bool HasPending => PendingCount > 0;

    /// <summary>«Ещё загружается: 1 файл».</summary>
    public string PendingText => L.F("Sorting.PendingFiles", Formatting.Count(PendingCount, L.T("Common.File.One"), L.T("Common.File.Few"), L.T("Common.File.Many")));

    /// <summary>Сколько файлов «оставлены здесь».</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasKept), nameof(KeptText))]
    public partial int KeptCount { get; set; }

    /// <summary>Есть оставленные здесь.</summary>
    public bool HasKept => KeptCount > 0;

    /// <summary>Показывать «оставленные здесь» в списке.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeptText))]
    public partial bool ShowKept { get; set; }

    /// <summary>Кнопка под списком: «Оставленные здесь: 3» или «Скрыть оставленные».</summary>
    public string KeptText => ShowKept ? L.T("Sorting.HideKept") : L.F("Sorting.ShowKept", KeptCount);

    /// <summary>Папка назначения выбрана.</summary>
    public bool HasDestination => SelectedFolder is not null;

    /// <summary>Куда пойдут файлы: «417 › Переписка».</summary>
    public string DestinationText => SelectedFolder is { } folder ? DescribePath(folder) : L.T("Sorting.ChooseDestination");

    /// <summary>Какие авто-теги файлы получат в выбранной папке (этап 19): «получат авто-теги: ТЗ, 417»; пусто — никаких.</summary>
    public string DestinationAutoTagsText => SelectedFolder is { } folder && FolderTagsInfo.InheritedAt(_autoTags, folder.Path) is { Count: > 0 } inherited
        ? L.F("Sorting.DestinationAutoTags", string.Join(", ", inherited.Select(static tag => tag.Name)))
        : string.Empty;

    /// <summary>Папка назначения даёт авто-теги.</summary>
    public bool HasDestinationAutoTags => DestinationAutoTagsText.Length > 0;

    /// <summary>Подпись кнопки переноса.</summary>
    public string MoveText => SelectedCount > 0
        ? L.F("Sorting.MoveItems", Formatting.Count(SelectedCount, L.T("Common.File.One"), L.T("Common.File.Few"), L.T("Common.File.Many")))
        : L.T("Sorting.Move");

    /// <summary>Шаблон имени задан: файлы переименуются при переносе.</summary>
    public bool HasNameTemplate => NameTemplate.Trim().Length > 0;

    /// <summary>Кнопка имени в полосе переноса: «Имя» или «Имя: {дата}_{имя}» — что задано, видно без раскрытия.</summary>
    public string NameButtonText => HasNameTemplate ? L.F("Sorting.NameButtonSet", NameTemplate.Trim()) : L.T("Sorting.NameButton");

    /// <summary>Заданы теги для переносимого.</summary>
    public bool HasTags => Tags.Count > 0;

    /// <summary>Кнопка тегов в полосе переноса: «Теги» или «Теги: ТЗ, 417».</summary>
    public string TagsButtonText => HasTags ? L.F("Sorting.TagsButtonSet", string.Join(", ", Tags)) : L.T("Sorting.TagsButton");

    /// <summary>Есть «Показать в папке».</summary>
    public bool CanReveal => _actions is not null;

    /// <summary>Ширина панели рабочих папок: тянется разделителем и запоминается между запусками.</summary>
    [ObservableProperty]
    public partial GridLength DestinationColumnWidth { get; set; }

    /// <summary>Завершается, когда закончилась последняя загрузка (для тестов).</summary>
    public Task PendingLoad { get; private set; } = Task.CompletedTask;

    /// <inheritdoc />
    public void OnNavigatedTo() => PendingLoad = RefreshAsync();

    /// <inheritdoc />
    public void OnNavigatedFrom()
    {
    }

    /// <summary>
    /// Перечитывает накопители, рабочие папки и теги.
    /// </summary>
    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            _folders = await _settings.GetAsync();
            if (!_nameTemplateLoaded)
            {
                NameTemplate = _folders.NameTemplate ?? string.Empty;
                _nameTemplateLoaded = true;
            }

            ShowWorkFolders();
            await LoadInboxesAsync();
            await LoadTagsAsync();
            await CheckWorkFoldersAsync();
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
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionHasState));
        UpdateItemsText();
        UpdateNamePreview();
    }

    /// <summary>Подставляет готовый шаблон имени.</summary>
    [RelayCommand]
    private void ApplyNamePreset(NamePreset? preset)
    {
        if (preset is not null)
        {
            NameTemplate = preset.Template;
        }
    }

    /// <summary>Дописывает подстановку в шаблон имени: <c>name</c>, <c>date</c>, <c>time</c>, <c>tags</c>, <c>folder</c>, <c>n</c>.</summary>
    [RelayCommand]
    private void InsertNameToken(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        var template = NameTemplate.TrimEnd();
        var separator = template.Length == 0 || template.EndsWith('_') || template.EndsWith('-') ? string.Empty : "_";
        NameTemplate = template + separator + Token(key);
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

    /// <summary>Снимает все теги переносимого.</summary>
    [RelayCommand(CanExecute = nameof(HasTags))]
    private void ClearTags()
    {
        Tags.Clear();
        TagText = string.Empty;
    }

    /// <summary>Убирает шаблон имени: файлы перенесутся с прежними именами.</summary>
    [RelayCommand(CanExecute = nameof(HasNameTemplate))]
    private void ClearNameTemplate() => NameTemplate = string.Empty;

    /// <summary>
    /// Открывает файл в его приложении — посмотреть, что внутри, прежде чем решить, куда его положить. Без аргумента —
    /// первый выбранный.
    /// </summary>
    [RelayCommand]
    private async Task OpenItemAsync(SortItem? item)
    {
        if ((item ?? (_selection.Count > 0 ? _selection[0] : null)) is not { } target)
        {
            return;
        }

        try
        {
            await _search.OpenAsync(target.Entry, null, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show(L.F("Sorting.OpenFailed", target.Entry.Name), exception.Message, FAInfoBarSeverity.Error);
        }
    }

    /// <summary>Показывает файл в Проводнике.</summary>
    [RelayCommand]
    private async Task RevealItemAsync(SortItem? item)
    {
        if (_actions is null || (item ?? (_selection.Count > 0 ? _selection[0] : null)) is not { } target)
        {
            return;
        }

        try
        {
            await _actions.RevealAsync(target.Entry);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show(L.F("Sorting.OpenFailed", target.Entry.Name), exception.Message, FAInfoBarSeverity.Error);
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
        var names = NewNames(items, tags, target);
        try
        {
            var operation = await _operations.SortAsync(new SortOperation([.. items.Select(static item => item.Entry.FullPath)], destination, newFolder, tags, names));
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

            await RememberNameTemplateAsync();

            var what = items.Length == 1 ? $"«{items[0].Entry.Name}»" : Formatting.Count(items.Length, L.T("Common.File.One"), L.T("Common.File.Few"), L.T("Common.File.Many"));
            var message = tags.Length > 0 ? L.F("Sorting.MovedWithTags", what, DescribePath(target), string.Join(", ", tags)) : L.F("Sorting.Moved", what, DescribePath(target));
            if (items.Length == 1 && names?[0] is { } renamed)
            {
                message += L.F("Sorting.RenamedTo", renamed);
            }
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

    // Подстановка в языке интерфейса: «{имя}» или «{name}»; шаблон понимает обе.
    private static string Token(string key) => L.T("Sorting.Token." + key);

    private static NamePreset[] BuildNamePresets() =>
    [
        new(L.T("Sorting.NameKeep"), string.Empty),
        new(L.T("Sorting.NameDateFirst"), Token("date") + "_" + Token("name")),
        new(L.T("Sorting.NameDateTimeFirst"), Token("date") + "_" + Token("time") + "_" + Token("name")),
        new(L.T("Sorting.NameTagsLast"), Token("name") + "_" + Token("tags")),
        new(L.T("Sorting.NameFolderFirst"), Token("folder") + "_" + Token("name")),
    ];

    // Новые имена по шаблону; null — шаблона нет или он ничего не меняет.
    private string?[]? NewNames(SortItem[] items, IReadOnlyList<string> tags, SortFolderNode target)
    {
        var template = NameTemplate.Trim();
        if (template.Length == 0)
        {
            return null;
        }

        var now = _clock();
        var folder = PathText.FileName(target.Path);
        var names = items
            .Select((item, index) => FileNameTemplate.Apply(template, new FileNameContext(item.Entry.Name, item.Entry.Kind == FileKind.Folder, index + 1, items.Length, tags, folder, now)))
            .ToArray();
        var changed = names.Select((name, index) => string.Equals(name, items[index].Entry.Name, StringComparison.Ordinal) ? null : name).ToArray();
        return changed.Any(static name => name is not null) ? changed : null;
    }

    private void UpdateNamePreview()
    {
        if (NameTemplate.Trim().Length == 0 || _selection.Count == 0 || SelectedFolder is not { } target)
        {
            NamePreview = string.Empty;
            return;
        }

        IReadOnlyList<string> tags = TagText.Trim().Length > 0 ? [.. Tags, TagText.Trim()] : [.. Tags];
        var first = FileNameTemplate.Apply(NameTemplate.Trim(), new FileNameContext(
            _selection[0].Entry.Name, _selection[0].Entry.Kind == FileKind.Folder, 1, _selection.Count, tags, PathText.FileName(target.Path), _clock()));
        NamePreview = _selection.Count > 1 ? L.F("Sorting.NamePreviewMany", first, _selection.Count - 1) : L.F("Sorting.NamePreview", first);
    }

    // Шаблон с прежним именем запоминается для следующих переносов; своё имя без {имя} — разовое и сбрасывается.
    private async Task RememberNameTemplateAsync()
    {
        var template = NameTemplate.Trim();
        var saved = _folders.NameTemplate ?? string.Empty;
        if (template.Length > 0 && !FileNameTemplate.HasNameToken(template))
        {
            NameTemplate = saved;
            return;
        }

        if (string.Equals(template, saved, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            _folders = _folders with { NameTemplate = template.Length == 0 ? null : template };
            await _settings.SaveAsync(_folders);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show(L.T("Sorting.Title"), L.F("Sorting.SaveFailed", exception.Message), FAInfoBarSeverity.Warning);
        }
    }

    /// <summary>Добавляет накопитель: выбор папки, затем — в индекс, если её там нет.</summary>
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

    /// <summary>Делает накопитель основным: сюда падают файлы из окна Drag&amp;Drop (этап 18.2).</summary>
    [RelayCommand]
    private async Task MakeMainInboxAsync(InboxFilter? inbox)
    {
        if (inbox?.Folder is { } folder && !inbox.IsMain)
        {
            await SaveAsync(_folders with { MainInbox = folder.Path }, null);
        }
    }

    /// <summary>Включать в очередь и файлы из подпапок накопителя (этап 17.1) — или перестать.</summary>
    [RelayCommand]
    private async Task ToggleSubfoldersAsync(InboxFilter? inbox)
    {
        if (inbox?.Folder is { } folder)
        {
            var updated = folder with { IncludeSubfolders = !folder.IncludeSubfolders };
            await SaveAsync(_folders with { Inboxes = [.. _folders.Inboxes.Select(candidate => SamePath(candidate.Path, folder.Path) ? updated : candidate)] }, null);
        }
    }

    /// <summary>Отложить выбранные файлы: они уходят в конец списка и не считаются в бейдже.</summary>
    [RelayCommand]
    private Task PostponeAsync() => SetSelectedStateAsync(InboxItemState.Postponed);

    /// <summary>«Оставить здесь»: файлы больше не предлагаются к разбору.</summary>
    [RelayCommand]
    private Task KeepHereAsync() => SetSelectedStateAsync(InboxItemState.Kept);

    /// <summary>Вернуть отложенные и оставленные файлы в новые.</summary>
    [RelayCommand]
    private Task ReturnToNewAsync() => SetSelectedStateAsync(InboxItemState.New);

    /// <summary>Показать или скрыть «оставленные здесь».</summary>
    [RelayCommand]
    private void ToggleShowKept() => ShowKept = !ShowKept;

    private async Task SetSelectedStateAsync(InboxItemState state)
    {
        var paths = _selection.Where(item => item.State != state).Select(static item => item.Entry.FullPath).ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        try
        {
            await _inbox.SetStateAsync(paths, state);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show(L.T("Sorting.Title"), L.F("Sorting.StateFailed", exception.Message), FAInfoBarSeverity.Error);
            return;
        }

        PendingLoad = LoadInboxesAsync();
        await PendingLoad;
    }

    /// <summary>Убирает накопитель из списка; папка и файлы остаются.</summary>
    [RelayCommand]
    private async Task RemoveInboxAsync(InboxFilter? inbox)
    {
        if (inbox?.Folder is { } folder)
        {
            // Убранная основная папка перестаёт быть основной: ею становится первая из оставшихся.
            await SaveAsync(
                _folders with
                {
                    Inboxes = [.. _folders.Inboxes.Where(candidate => !SamePath(candidate.Path, folder.Path))],
                    MainInbox = _folders.MainInbox is { } main && SamePath(folder.Path, main) ? null : _folders.MainInbox,
                },
                null);
        }
    }

    /// <summary>
    /// Убирает рабочую папку из списка; папка и файлы остаются. Если у папки есть авто-теги (этап 19), спрашивает, снять ли
    /// их: оставленные продолжают действовать — папку можно убрать из «Разбора» только ради порядка клавиш.
    /// </summary>
    [RelayCommand]
    private async Task RemoveWorkFolderAsync(WorkFolderItem? item)
    {
        if (item is null)
        {
            return;
        }

        var path = item.Folder.Path;
        await SaveAsync(_folders with { WorkFolders = [.. _folders.WorkFolders.Where(candidate => !SamePath(candidate.Path, path))] }, null);
        if (OwnAutoTags(path) is not { Count: > 0 } autoTags || _dialogs is null)
        {
            return;
        }

        var names = string.Join(", ", autoTags.Select(static tag => tag.Name));
        if (await _dialogs.ConfirmAsync(L.T("Sorting.RemoveAutoTagsTitle"), L.F("Sorting.RemoveAutoTagsMessage", PathText.FileName(path), names), L.T("Sorting.RemoveAutoTags"), L.T("Sorting.KeepAutoTags")))
        {
            await SaveAutoTagsAsync(() => _library.SetAutoTagsAsync(path, []));
        }
    }

    /// <summary>
    /// «Авто-теги…» в меню чипа рабочей папки (этап 19): тот же редактор, что в карточке, но в один шаг — без подписи и номера.
    /// </summary>
    [RelayCommand]
    private async Task EditWorkFolderAutoTagsAsync(WorkFolderItem? item)
    {
        if (item is null || _dialogs is null)
        {
            return;
        }

        AutoTagsEditorViewModel editor;
        try
        {
            editor = await AutoTagsEditorViewModel.LoadAsync(_library, item.Folder.Path);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show(L.T("AutoTags.Title"), L.F("AutoTags.LoadFailed", exception.Message), FAInfoBarSeverity.Error);
            return;
        }

        if (await _dialogs.ShowAutoTagsAsync(item.Folder.Title, editor) && editor.IsChanged)
        {
            await SaveAutoTagsAsync(() => editor.SaveAsync(_library, item.Folder.Path));
        }
    }

    /// <summary>Сохраняет авто-теги и перечитывает их; ошибку показывает полосой уведомлений.</summary>
    private async Task SaveAutoTagsAsync(Func<Task> save)
    {
        try
        {
            await save();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show(L.T("AutoTags.Title"), L.F("AutoTags.SaveFailed", exception.Message), FAInfoBarSeverity.Error);
        }

        await LoadTagsAsync();
    }

    /// <summary>Сдвигает рабочую папку на одну клавишу раньше.</summary>
    [RelayCommand]
    private Task MoveWorkFolderUpAsync(WorkFolderItem? item) => ShiftWorkFolderAsync(item, -1);

    /// <summary>Сдвигает рабочую папку на одну клавишу позже.</summary>
    [RelayCommand]
    private Task MoveWorkFolderDownAsync(WorkFolderItem? item) => ShiftWorkFolderAsync(item, 1);

    /// <summary>Перечитывает накопители.</summary>
    [RelayCommand]
    private Task Refresh() => PendingLoad = RefreshAsync();

    partial void OnSelectedInboxChanged(InboxFilter? value) => ShowItems();

    partial void OnShowKeptChanged(bool value) => ShowItems(keepSelection: true);

    partial void OnNameTemplateChanged(string value)
    {
        UpdateNamePreview();
        OnPropertyChanged(nameof(HasNameTemplate));
        OnPropertyChanged(nameof(NameButtonText));
        ClearNameTemplateCommand.NotifyCanExecuteChanged();
    }

    partial void OnDestinationColumnWidthChanged(GridLength value)
    {
        if (value.IsAbsolute)
        {
            _layout?.SetWidth(DestinationsLayoutKey, value.Value);
        }
    }

    partial void OnSelectedNamePresetChanged(NamePreset? value)
    {
        if (value is not null)
        {
            ApplyNamePreset(value);
            SelectedNamePreset = null;
        }
    }

    partial void OnSelectedFolderChanged(SortFolderNode? value) => UpdateNamePreview();

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
        UpdateNamePreview();
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
        NamePresets = BuildNamePresets();
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
            WorkFolders.Add(new WorkFolderItem(i < 9 ? i + 1 : 0, _folders.WorkFolders[i], _missing.Contains(_folders.WorkFolders[i].Path)));
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

    /// <summary>
    /// Перечитывает очередь накопителей (этап 17.1): пришедшие файлы — новые, затем отложенные; «оставленные здесь» — по
    /// кнопке; ещё дописываемые — только числом. Если состав накопителей тот же, чипы и выделение в списке не сбрасываются:
    /// очередь обновляется и сама, когда что-то скачалось.
    /// </summary>
    private Task LoadInboxesAsync()
    {
        // Чтения идут по одному: бейдж при запуске, вход на страницу, событие хаба и таймер могут совпасть, а чипы и список
        // перестраиваются целиком.
        var done = new TaskCompletionSource();
        var previous = Interlocked.Exchange(ref _inboxLoad, done.Task);
        return RunAsync();

        async Task RunAsync()
        {
            try
            {
                await previous;
                await LoadInboxesCoreAsync();
            }
            finally
            {
                done.SetResult();
            }
        }
    }

    private async Task LoadInboxesCoreAsync()
    {
        InboxSnapshotInfo queue;
        try
        {
            queue = await _inbox.GetAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            queue = new InboxSnapshotInfo([.. _folders.Inboxes.Select(folder => new InboxFolderState(folder.Path, exception.Message))], [], null);
        }

        // Обновления могут пересечься — бейдж при запуске и вход на страницу: список собирается целиком и подменяется
        // за раз, иначе файлы удвоятся.
        var items = new List<SortItem>();
        var pending = 0;
        foreach (var item in queue.Items)
        {
            if (_folders.Inboxes.FirstOrDefault(folder => SamePath(folder.Path, item.Inbox)) is not { } folder)
            {
                continue;
            }

            if (item.IsReady)
            {
                items.Add(new SortItem(item.Entry, folder, item.State));
            }
            else
            {
                pending++;
            }
        }

        _all.Clear();
        _all.AddRange(items.OrderBy(static item => item.State));
        PendingCount = pending;
        HasNoInboxes = _folders.Inboxes.Count == 0;
        ScheduleReadyRefresh(queue.NextReadyUtc);

        var problems = queue.Folders.GroupBy(static folder => folder.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(static group => group.Key, static group => group.First().Problem, StringComparer.OrdinalIgnoreCase);
        if (Inboxes.Count == _folders.Inboxes.Count + 1 && Inboxes.Skip(1).Select(static filter => filter.Folder).SequenceEqual(_folders.Inboxes))
        {
            foreach (var filter in Inboxes.Skip(1))
            {
                filter.Problem = problems.GetValueOrDefault(filter.Folder!.Path);
                filter.IsMain = _folders.Main == filter.Folder;
            }

            UpdateCounts();
            ShowItems(keepSelection: true);
            return;
        }

        var selected = SelectedInbox?.Folder?.Path;
        var all = new InboxFilter(null, L.T("Sorting.AllInboxes"));
        Inboxes.Clear();
        Inboxes.Add(all);
        foreach (var folder in _folders.Inboxes)
        {
            Inboxes.Add(new InboxFilter(folder, folder.Title) { IsMain = _folders.Main == folder, Problem = problems.GetValueOrDefault(folder.Path) });
        }

        UpdateCounts();
        SelectedInbox = Inboxes.FirstOrDefault(filter => filter.Folder is not null && selected is not null && SamePath(filter.Folder.Path, selected)) ?? all;
        ShowItems();
    }

    // В накопителях что-то изменилось мимо страницы — скачалось, переименовано: очередь перечитывается через короткую паузу,
    // чтобы пачка событий дала одно обновление.
    private void OnInboxChanged(object? sender, EventArgs e)
    {
        if (_dispatcher is null)
        {
            ScheduleInboxReload(TimeSpan.FromMilliseconds(400));
        }
        else
        {
            _dispatcher.Post(() => ScheduleInboxReload(TimeSpan.FromMilliseconds(400)));
        }
    }

    // Дописываемый файл придёт, когда истечёт «время успокоения»: тогда очередь перечитывается.
    private void ScheduleReadyRefresh(DateTimeOffset? nextReadyUtc)
    {
        Interlocked.Exchange(ref _readyTimer, null)?.Cancel();
        if (nextReadyUtc is not { } next)
        {
            return;
        }

        var delay = next - _clock();
        delay = delay < TimeSpan.FromMilliseconds(200) ? TimeSpan.FromMilliseconds(200) : delay > TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(10) : delay;
        var cts = new CancellationTokenSource();
        _readyTimer = cts;
        _ = ReloadAfterAsync(delay, cts.Token);
    }

    private void ScheduleInboxReload(TimeSpan delay)
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _inboxReload, cts)?.Cancel();
        _ = ReloadAfterAsync(delay, cts.Token);
    }

    private async Task ReloadAfterAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        PendingLoad = LoadInboxesAsync();
        await PendingLoad;
    }

    private async Task LoadTagsAsync()
    {
        try
        {
            _tagInfos = await _library.GetTagsAsync();
            _allTags = [.. _tagInfos.Select(static tag => tag.Name)];
            _autoTags = await _library.GetAutoTagsAsync() ?? [];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Без подсказок теги всё равно вводятся вручную, а без авто-тегов просто нет строки «получат авто-теги».
            _ = exception;
        }

        OnPropertyChanged(nameof(DestinationAutoTagsText));
        OnPropertyChanged(nameof(HasDestinationAutoTags));
    }

    /// <summary>Авто-теги папки ровно по этому пути (не унаследованные от папок выше).</summary>
    private IReadOnlyList<TagInfo> OwnAutoTags(string path)
        => _autoTags.FirstOrDefault(folder => SamePath(folder.Path, path))?.Tags ?? [];

    /// <summary>
    /// Показывает файлы выбранного накопителя. <paramref name="keepSelection"/> — обновление на месте: те же файлы остаются
    /// теми же строками, поэтому выделение в списке не пропадает, когда в «Загрузки» что-то пришло.
    /// </summary>
    private void ShowItems(bool keepSelection = false)
    {
        var folder = SelectedInbox?.Folder;
        var wanted = _all.Where(item => (folder is null || SamePath(item.Inbox.Path, folder.Path)) && (ShowKept || !item.IsKept)).ToList();
        if (!keepSelection)
        {
            Items.Clear();
            foreach (var item in wanted)
            {
                Items.Add(item);
            }

            SetSelection([]);
        }
        else
        {
            var existing = Items.GroupBy(static item => item.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < wanted.Count; i++)
            {
                if (existing.TryGetValue(wanted[i].Key, out var same))
                {
                    wanted[i] = same;
                }
            }

            var keep = wanted.ToHashSet();
            for (var i = Items.Count - 1; i >= 0; i--)
            {
                if (!keep.Contains(Items[i]))
                {
                    Items.RemoveAt(i);
                }
            }

            for (var i = 0; i < wanted.Count; i++)
            {
                if (i >= Items.Count || !ReferenceEquals(Items[i], wanted[i]))
                {
                    Items.Insert(i, wanted[i]);
                }
            }

            SetSelection([.. _selection.Where(Items.Contains)]);
        }

        EmptyHint = HasNoInboxes
            ? L.T("Sorting.NoInboxesHint")
            : Items.Count == 0 ? (SelectedInbox?.Problem is { } problem ? L.F("Sorting.InboxProblem", problem) : L.T("Sorting.InboxEmpty")) : string.Empty;
    }

    private void UpdateCounts()
    {
        foreach (var filter in Inboxes)
        {
            filter.Count = _all.Count(item => item.State == InboxItemState.New && (filter.Folder is null || SamePath(item.Inbox.Path, filter.Folder.Path)));
        }

        BadgeValue = _all.Count(static item => item.State == InboxItemState.New);
        KeptCount = _all.Count(static item => item.IsKept);
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
        var token = cts.Token;
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
            var result = await _search.SearchAsync("is:dir " + text, token);
            token.ThrowIfCancellationRequested();
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

    /// <summary>
    /// Карточка рабочей папки (этап 17.2): подпись, номер-клавиша и сама папка — потерянную можно выбрать заново.
    /// </summary>
    [RelayCommand]
    private async Task EditWorkFolderAsync(WorkFolderItem? item)
    {
        if (item is null || _dialogs is null)
        {
            return;
        }

        var list = _folders.WorkFolders.ToList();
        var index = list.FindIndex(folder => SamePath(folder.Path, item.Folder.Path));
        if (index < 0)
        {
            return;
        }

        // Авто-теги читаются заново: их могли поменять в «Обзоре». Не прочитались — поле не показывается, чтобы сохранение
        // карточки не стёрло их.
        AutoTagsEditorViewModel? autoTags = null;
        try
        {
            autoTags = await AutoTagsEditorViewModel.LoadAsync(_library, item.Folder.Path);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _ = exception;
        }

        var card = new WorkFolderCardViewModel(item.Folder, index, list.Count, item.IsMissing, _dialogs, autoTags);
        switch (await _dialogs.ShowWorkFolderCardAsync(card))
        {
            case WorkFolderCardResult.Removed:
                await RemoveWorkFolderAsync(item);
                break;
            case WorkFolderCardResult.Saved:
                var edited = card.ToFolder();
                if (list.Where((_, position) => position != index).Any(folder => SamePath(folder.Path, edited.Path)))
                {
                    _notices?.Show(L.T("Sorting.Title"), L.F("Sorting.WorkFolderDuplicate", edited.Path), FAInfoBarSeverity.Warning);
                    return;
                }

                list.RemoveAt(index);
                list.Insert(Math.Clamp(card.SelectedPosition.Value, 0, list.Count), edited);
                var moved = !SamePath(edited.Path, item.Folder.Path);
                await SaveAsync(_folders with { WorkFolders = list }, moved ? edited.Path : null);
                if (autoTags is not null && (autoTags.IsChanged || (moved && autoTags.HasTags)))
                {
                    // Папку выбрали заново — авто-теги переезжают к ней вместе с карточкой.
                    await SaveAutoTagsAsync(async () =>
                    {
                        if (moved && autoTags.HadTags)
                        {
                            await _library.SetAutoTagsAsync(item.Folder.Path, []);
                        }

                        await autoTags.SaveAsync(_library, edited.Path);
                    });
                }

                break;
        }
    }

    /// <summary>
    /// Отмечает рабочие папки, которых нет в индексе (этап 17.2): переименованную в Проводнике сервер переносит сам, а
    /// перенесённую в другое место или удалённую — нет, и пользователь видит это на чипе и в карточке.
    /// </summary>
    private async Task CheckWorkFoldersAsync()
    {
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_folders.WorkFolders.Count > 0)
        {
            try
            {
                missing.UnionWith((await _library.ResolveAsync([.. _folders.WorkFolders.Select(static folder => folder.Path)])).Missing);
                if (missing.Count > 0)
                {
                    // Папка вне индекса добавляется корнем, а у самого корня записи в индексе нет — он есть, если доступен.
                    var roots = await _roots.GetRootsAsync();
                    missing.RemoveWhere(path => roots.Any(root => SamePath(root.Path, path) && root.State is not (RootState.Offline or RootState.Error)));
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Без ответа сервера пометок нет: лучше не пугать «не найдена» зря.
                return;
            }
        }

        if (!missing.SetEquals(_missing))
        {
            _missing = missing;
            ShowWorkFolders();
        }
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
            await CheckWorkFoldersAsync();
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

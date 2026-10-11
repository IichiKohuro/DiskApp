using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DiskApp.UI.Localization;
using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.Text;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.ViewModels;

/// <summary>
/// Главная страница (PLAN.md, этап 8.2): приветствие, состояние индекса, карточки корней с действиями, недавно
/// открытые, закреплённые и изменённые за сутки файлы, быстрые действия и добавление папок в индекс.
/// </summary>
public sealed partial class HomeViewModel : PageViewModel
{
    private const int ListCount = 6;

    private readonly ISearchService _search;
    private readonly IAppCommands _commands;
    private readonly IRootsService _roots;
    private readonly IInfoBarService? _notices;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>
    /// Создаёт страницу с макетными корнями (превью) и сразу запускает загрузку данных (<see cref="Initialization"/>).
    /// </summary>
    public HomeViewModel(ISearchService search, IAppCommands commands, HotkeyStatus hotkeys)
        : this(search, commands, hotkeys, new SampleRootsService(), null, static () => DateTimeOffset.Now)
    {
    }

    /// <summary>
    /// Создаёт страницу приложения.
    /// </summary>
    public HomeViewModel(ISearchService search, IAppCommands commands, HotkeyStatus hotkeys, IRootsService roots, IInfoBarService notices)
        : this(search, commands, hotkeys, roots, notices, static () => DateTimeOffset.Now)
    {
    }

    /// <summary>
    /// Создаёт страницу с заданными часами и макетными корнями (для тестов).
    /// </summary>
    public HomeViewModel(ISearchService search, IAppCommands commands, HotkeyStatus hotkeys, Func<DateTimeOffset> clock)
        : this(search, commands, hotkeys, new SampleRootsService(), null, clock)
    {
    }

    /// <summary>
    /// Создаёт страницу с заданными корнями, уведомлениями и часами (для тестов).
    /// </summary>
    public HomeViewModel(ISearchService search, IAppCommands commands, HotkeyStatus hotkeys, IRootsService roots, IInfoBarService? notices, Func<DateTimeOffset> clock)
        : base(PageKeys.Home, "Home.Home", FASymbol.Home)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(hotkeys);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(clock);

        _search = search;
        _commands = commands;
        _roots = roots;
        _notices = notices;
        _clock = clock;
        Hotkeys = hotkeys;

        Greeting = GreetingFor(clock().Hour);
        IndexStatusText = string.Empty;
        InboxText = string.Empty;
        LastOpenedText = string.Empty;
        Initialization = RefreshAsync();
    }

    /// <summary>Завершается, когда первая загрузка данных закончена; нужно тестам и оболочке.</summary>
    public Task Initialization { get; }

    /// <summary>Действующее сочетание быстрого поиска для подсказок.</summary>
    public HotkeyStatus Hotkeys { get; }

    /// <summary>Приветствие по времени суток.</summary>
    [ObservableProperty]
    public partial string Greeting { get; set; }

    /// <summary>Подпись состояния индекса: число файлов, корни, свежесть.</summary>
    [ObservableProperty]
    public partial string IndexStatusText { get; set; }

    /// <summary>Идёт ли индексация.</summary>
    [ObservableProperty]
    public partial bool IsIndexing { get; set; }

    /// <summary>Доля выполнения индексации от 0 до 100 для индикатора.</summary>
    [ObservableProperty]
    public partial double IndexProgressPercent { get; set; }

    /// <summary>Объём индексации заранее неизвестен: индикатор бегущий.</summary>
    [ObservableProperty]
    public partial bool IsIndexProgressIndeterminate { get; set; }

    /// <summary>Сколько файлов ждёт разбора во «входящих».</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInbox))]
    public partial int InboxCount { get; set; }

    /// <summary>Подпись карточки входящих: «12 файлов ждут разбора в «Загрузках»».</summary>
    [ObservableProperty]
    public partial string InboxText { get; set; }

    /// <summary>Сообщение о последнем открытом файле (макет: настоящее открытие — этап 7.3).</summary>
    [ObservableProperty]
    public partial string LastOpenedText { get; set; }

    /// <summary>Есть ли что разбирать.</summary>
    public bool HasInbox => InboxCount > 0;

    /// <summary>Недавно открытые файлы, самые свежие первыми.</summary>
    public ObservableCollection<SearchHit> RecentFiles { get; } = [];

    /// <summary>Закреплённые (избранное).</summary>
    public ObservableCollection<SearchHit> PinnedFiles { get; } = [];

    /// <summary>Изменённые за последние сутки.</summary>
    public ObservableCollection<SearchHit> ChangedToday { get; } = [];

    /// <summary>Корни индекса.</summary>
    public ObservableCollection<RootCardViewModel> Roots { get; } = [];

    /// <summary>Есть ли закреплённые.</summary>
    public bool HasPinned => PinnedFiles.Count > 0;

    /// <summary>Есть ли изменённые за сутки.</summary>
    public bool HasChangedToday => ChangedToday.Count > 0;

    /// <summary>Корней нет: показать приглашение добавить папку.</summary>
    public bool HasNoRoots => Roots.Count == 0;

    /// <inheritdoc />
    protected override Task RelocalizeAsync() => RefreshAsync();

    /// <summary>
    /// Перечитывает состояние индекса и список недавних файлов.
    /// </summary>
    public async Task RefreshAsync()
    {
        Greeting = GreetingFor(_clock().Hour);
        var status = await _search.GetIndexStatusAsync(CancellationToken.None);
        var recent = await _search.GetRecentAsync(CancellationToken.None);
        var sections = await _search.GetSuggestionsAsync(CancellationToken.None);
        var changed = await _search.GetChangedSinceAsync(TimeSpan.FromDays(1), CancellationToken.None);
        var now = _clock();

        IndexStatusText = status.Describe(now);
        IsIndexing = status.IsIndexing;
        IndexProgressPercent = (status.Progress ?? 0) * 100;
        IsIndexProgressIndeterminate = status.Progress is null;
        InboxCount = status.InboxCount;
        InboxText = status.InboxCount > 0
            ? L.F("Home.InboxWaiting", Formatting.Count(status.InboxCount, L.T("Home.FileWaits.One"), L.T("Home.FileWaits.Few"), L.T("Home.FileWaits.Many")))
            : L.T("Home.InboxIsEmptyEverythingIsSorted");

        Fill(RecentFiles, recent, now);
        Fill(PinnedFiles, sections.FirstOrDefault(static section => section.Title == SectionTitles.Pinned)?.Entries ?? [], now);
        Fill(ChangedToday, changed, now);
        OnPropertyChanged(nameof(HasPinned));
        OnPropertyChanged(nameof(HasChangedToday));

        await RefreshRootsAsync();
    }

    /// <summary>
    /// Перечитывает корни и их сводки.
    /// </summary>
    public async Task RefreshRootsAsync()
    {
        IReadOnlyList<RootInfo> roots;
        try
        {
            roots = await _roots.GetRootsAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportError(L.T("Home.CouldNotGetTheRoots"), exception);
            return;
        }

        Roots.Clear();
        foreach (var root in roots)
        {
            Roots.Add(new RootCardViewModel(root, _roots, _clock, ReportError));
        }

        OnPropertyChanged(nameof(HasNoRoots));
        await Task.WhenAll(Roots.Select(static card => card.LoadSummaryAsync()));
    }

    /// <summary>
    /// Добавляет папки в индекс: из выбора папки или перетаскиванием на главную.
    /// </summary>
    /// <returns>Сколько папок добавлено.</returns>
    public async Task<int> AddRootsAsync(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var added = new List<string>();
        var failed = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                added.Add((await _roots.AddAsync(path)).Label);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failed.Add($"«{path}»: {exception.Message}");
            }
        }

        if (added.Count > 0)
        {
            _notices?.Show(L.T("Home.FoldersAddedToTheIndex"), string.Join(", ", added) + L.T("Home.IndexingIsAlreadyRunning"), FAInfoBarSeverity.Success);
            await RefreshRootsAsync();
        }

        if (failed.Count > 0)
        {
            _notices?.Show(L.T("Home.NotAllFoldersWereAdded"), string.Join("; ", failed), FAInfoBarSeverity.Warning);
        }

        return added.Count;
    }

    /// <summary>
    /// Сценарий «Добавить» для брошенных на главную файлов: выбрать место и положить туда.
    /// </summary>
    public Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return _commands.AddFilesAsync(paths);
    }

    /// <summary>Показывает ошибку действия на главной.</summary>
    public void ReportError(string title, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _notices?.Show(title, exception.Message, FAInfoBarSeverity.Error);
    }

    private static void Fill(ObservableCollection<SearchHit> target, IReadOnlyList<FileEntry> entries, DateTimeOffset now)
    {
        target.Clear();
        foreach (var entry in entries.Take(ListCount))
        {
            target.Add(new SearchHit(entry, string.Empty, 0, now));
        }
    }

    /// <summary>
    /// Приветствие по часу суток: утро с 5 до 11, день до 17, вечер до 22, ночь — остальное.
    /// </summary>
    public static string GreetingFor(int hour) => hour switch
    {
        >= 5 and < 12 => L.T("Home.GoodMorning"),
        >= 12 and < 18 => L.T("Home.GoodAfternoon"),
        >= 18 and < 23 => L.T("Home.GoodEvening"),
        _ => L.T("Home.GoodNight"),
    };

    [RelayCommand]
    private void OpenQuickSearch() => _commands.ShowQuickSearch();

    [RelayCommand]
    private void OpenInbox() => _commands.NavigateTo(PageKeys.Inbox);

    [RelayCommand]
    private void OpenBrowse() => _commands.NavigateTo(PageKeys.Browse);

    [RelayCommand]
    private void OpenSettings() => _commands.NavigateTo(PageKeys.Settings);

    [RelayCommand]
    private async Task OpenFileAsync(SearchHit? hit)
    {
        if (hit is null)
        {
            return;
        }

        try
        {
            await _search.OpenAsync(hit.Entry, null, CancellationToken.None);
            LastOpenedText = L.F("Home.Opened", hit.Entry.Name);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ReportError(L.F("Home.CouldNotOpen", hit.Entry.Name), exception);
        }

        await RefreshAsync();
    }
}

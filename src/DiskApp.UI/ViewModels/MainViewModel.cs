using CommunityToolkit.Mvvm.ComponentModel;

using DiskApp.UI.Localization;
using DiskApp.UI.Services;

using FluentAvalonia.UI.Controls;

using FluentIcons.Common;

namespace DiskApp.UI.ViewModels;

/// <summary>
/// Оболочка главного окна: список страниц для <c>NavigationView</c>, выбранная страница,
/// уведомление в <c>InfoBar</c> и состояние индекса в подвале панели навигации.
/// </summary>
public sealed partial class MainViewModel : ViewModelBase
{
    private readonly ISearchService _search;
    private readonly QuickSearchViewModel _quickSearch;
    private bool _healthFromServer;
    private Action? _noticeAction;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>
    /// Создаёт оболочку приложения и запускает загрузку состояния индекса (<see cref="Initialization"/>).
    /// </summary>
    public MainViewModel(HomeViewModel home, QuickSearchViewModel search, BrowseViewModel browse, OperationsViewModel operations, SettingsViewModel settings, ISearchService searchService)
        : this(home, search, browse, operations, settings, searchService, static () => DateTimeOffset.Now)
    {
    }

    /// <summary>
    /// Создаёт оболочку с макетным обзором (превью).
    /// </summary>
    public MainViewModel(HomeViewModel home, QuickSearchViewModel search, SettingsViewModel settings, ISearchService searchService)
        : this(home, search, settings, searchService, static () => DateTimeOffset.Now)
    {
    }

    /// <summary>
    /// Создаёт оболочку с заданными часами и макетным обзором (для тестов и превью).
    /// </summary>
    public MainViewModel(HomeViewModel home, QuickSearchViewModel search, SettingsViewModel settings, ISearchService searchService, Func<DateTimeOffset> clock)
        : this(home, search, SampleBrowse(searchService, clock), new OperationsViewModel(new SampleOperationsService(), new ImmediateDispatcher()), settings, searchService, clock)
    {
    }

    /// <summary>
    /// Создаёт оболочку с заданными страницами и часами.
    /// </summary>
    public MainViewModel(HomeViewModel home, QuickSearchViewModel search, BrowseViewModel browse, OperationsViewModel operations, SettingsViewModel settings, ISearchService searchService, Func<DateTimeOffset> clock)
        : this(home, search, browse, operations, CollectionsPlaceholder(), TagsPlaceholder(), settings, searchService, clock)
    {
    }

    /// <summary>
    /// Создаёт главное окно приложения со всеми страницами, включая «Коллекции» и «Теги» (PLAN.md, этап 8.7).
    /// </summary>
    public MainViewModel(
        HomeViewModel home,
        QuickSearchViewModel search,
        BrowseViewModel browse,
        OperationsViewModel operations,
        CollectionsViewModel collections,
        TagsViewModel tags,
        SettingsViewModel settings,
        ISearchService searchService)
        : this(home, search, browse, operations, collections, tags, settings, searchService, static () => DateTimeOffset.Now)
    {
    }

    private MainViewModel(
        HomeViewModel home,
        QuickSearchViewModel search,
        BrowseViewModel browse,
        OperationsViewModel operations,
        PageViewModel collections,
        PageViewModel tags,
        SettingsViewModel settings,
        ISearchService searchService,
        Func<DateTimeOffset> clock)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(browse);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(collections);
        ArgumentNullException.ThrowIfNull(tags);
        Operations = operations;
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(searchService);
        ArgumentNullException.ThrowIfNull(clock);

        _search = searchService;
        _quickSearch = search;
        _clock = clock;

        Pages =
        [
            home,
            search,
            browse,
            collections,
            tags,
            new PlaceholderPageViewModel(
                PageKeys.Inbox, "Main.Inbox", FASymbol.Download, Symbol.MailInbox,
                "Main.SortingDownloadsIntoDedicatedStoresA",
                "Main.Stage17"),
            operations,
        ];
        FooterPages = [settings];
        SelectedPage = home;

        NoticeTitle = string.Empty;
        NoticeMessage = string.Empty;
        NoticeSeverity = FAInfoBarSeverity.Informational;
        IndexStatusText = string.Empty;
        IndexStatusShort = string.Empty;

        Initialization = RefreshAsync();

        // Состояние индекса собирается в коде: после смены языка его подписи перечитываются (этап 9.2).
        var main = new WeakReference<MainViewModel>(this);
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (main.TryGetTarget(out var target))
            {
                _ = target.RefreshAsync();
            }
            else
            {
                Localizer.Instance.LanguageChanged -= handler;
            }
        };
        Localizer.Instance.LanguageChanged += handler;
    }

    /// <summary>Завершается, когда состояние индекса загружено.</summary>
    public Task Initialization { get; }

    /// <summary>Страница и координатор файловых операций: полоса хода, <c>Ctrl+Z</c>.</summary>
    public OperationsViewModel Operations { get; }

    /// <summary>Страницы в основной части панели навигации.</summary>
    public IReadOnlyList<PageViewModel> Pages { get; }

    /// <summary>Страницы в подвале панели навигации (настройки).</summary>
    public IReadOnlyList<PageViewModel> FooterPages { get; }

    /// <summary>Выбранная страница.</summary>
    [ObservableProperty]
    public partial PageViewModel? SelectedPage { get; set; }

    /// <summary>Открыто ли уведомление.</summary>
    [ObservableProperty]
    public partial bool IsNoticeOpen { get; set; }

    /// <summary>Заголовок уведомления.</summary>
    [ObservableProperty]
    public partial string NoticeTitle { get; set; }

    /// <summary>Текст уведомления.</summary>
    [ObservableProperty]
    public partial string NoticeMessage { get; set; }

    /// <summary>Важность уведомления.</summary>
    [ObservableProperty]
    public partial FAInfoBarSeverity NoticeSeverity { get; set; }

    /// <summary>Полная подпись состояния индекса.</summary>
    [ObservableProperty]
    public partial string IndexStatusText { get; set; }

    /// <summary>Короткая подпись для подвала панели навигации: «Индекс актуален» или «Индексация 43 %».</summary>
    [ObservableProperty]
    public partial string IndexStatusShort { get; set; }

    /// <summary>Идёт ли индексация.</summary>
    [ObservableProperty]
    public partial bool IsIndexing { get; set; }

    /// <summary>«Уменьшить движение»: страницы сменяются без анимации (PLAN.md, этап 6.8).</summary>
    [ObservableProperty]
    public partial bool ReduceMotion { get; set; }

    /// <summary>Мастер первого запуска; <c>null</c> — не показывается (этап 9.3).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnboarding))]
    public partial OnboardingViewModel? Onboarding { get; set; }

    /// <summary>Мастер первого запуска открыт.</summary>
    public bool IsOnboarding => Onboarding is not null;

    /// <summary>
    /// Показывает мастер первого запуска поверх страниц; когда он закрывается, открывается главная.
    /// </summary>
    public void StartOnboarding(OnboardingViewModel onboarding)
    {
        ArgumentNullException.ThrowIfNull(onboarding);
        onboarding.Completed += (_, _) =>
        {
            Onboarding = null;
            onboarding.Dispose();
            NavigateTo(PageKeys.Home);
            _ = RefreshAsync();
        };
        Onboarding = onboarding;
    }

    /// <summary>
    /// Переходит на страницу по ключу.
    /// </summary>
    /// <returns><c>true</c>, если страница найдена.</returns>
    /// <summary>Страница «Коллекции» без библиотеки (превью, тесты оболочки).</summary>
    private static PlaceholderPageViewModel CollectionsPlaceholder() => new(
        PageKeys.Collections, "Common.Page.Collections", FASymbol.Library, Symbol.Collections,
        "Main.FavoritesPinnedFoldersAndSetsOf",
        "Main.Stage8");

    /// <summary>Страница «Теги» без библиотеки (превью, тесты оболочки).</summary>
    private static PlaceholderPageViewModel TagsPlaceholder() => new(
        PageKeys.Tags, "Common.Page.Tags", FASymbol.Tag, Symbol.TagMultiple,
        "Main.TagsAndColorsForFilesAnd",
        "Main.Stage8");

    partial void OnSelectedPageChanged(PageViewModel? oldValue, PageViewModel? newValue)
    {
        (oldValue as IPageLifecycle)?.OnNavigatedFrom();
        (newValue as IPageLifecycle)?.OnNavigatedTo();
    }

    public bool NavigateTo(string pageKey)
    {
        var page = Pages.Concat(FooterPages).FirstOrDefault(candidate => candidate.Key == pageKey);
        if (page is null)
        {
            return false;
        }

        SelectedPage = page;
        return true;
    }

    /// <summary>
    /// Показывает уведомление в <c>InfoBar</c> над содержимым страницы.
    /// </summary>
    public void ShowNotice(string title, string message, FAInfoBarSeverity severity = FAInfoBarSeverity.Informational)
    {
        _noticeAction = null;
        NoticeActionText = string.Empty;
        NoticeTitle = title;
        NoticeMessage = message;
        NoticeSeverity = severity;
        IsNoticeOpen = true;
    }

    /// <summary>
    /// Показывает уведомление с кнопкой действия («Отменить» после операции).
    /// </summary>
    public void ShowNotice(string title, string message, FAInfoBarSeverity severity, string actionText, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ShowNotice(title, message, severity);
        _noticeAction = action;
        NoticeActionText = actionText;
    }

    /// <summary>Подпись кнопки действия в уведомлении; пусто — кнопки нет.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoticeAction))]
    public partial string NoticeActionText { get; set; } = string.Empty;

    /// <summary>У уведомления есть кнопка действия.</summary>
    public bool HasNoticeAction => NoticeActionText.Length > 0;

    /// <summary>Выполняет действие уведомления и закрывает его.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void RunNoticeAction()
    {
        var action = _noticeAction;
        IsNoticeOpen = false;
        _noticeAction = null;
        NoticeActionText = string.Empty;
        action?.Invoke();
    }

    /// <summary>
    /// Перечитывает состояние индекса: подписи и счётчик входящих.
    /// </summary>
    public async Task RefreshAsync()
    {
        var status = await _search.GetIndexStatusAsync(CancellationToken.None);

        IndexStatusText = status.Describe(_clock());

        // Когда состояние приходит событиями сервера (ApplyIndexHealth), оно точнее разового запроса.
        if (!_healthFromServer)
        {
            IsIndexing = status.IsIndexing;
            IndexStatusShort = status.IsIndexing ? Capitalize(status.DescribeIndexing()) : L.T("Main.IndexIsUpToDate");
        }

        if (Pages.FirstOrDefault(page => page.Key == PageKeys.Inbox) is { } inbox)
        {
            inbox.BadgeValue = status.InboxCount;
        }
    }

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static BrowseViewModel SampleBrowse(ISearchService search, Func<DateTimeOffset> clock)
        => new(new SampleBrowseService(clock), new SampleRootsService(), search, new SampleFileActions(), null, null, clock);
}

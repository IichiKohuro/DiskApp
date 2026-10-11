using CommunityToolkit.Mvvm.ComponentModel;

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
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>
    /// Создаёт оболочку с готовыми страницами и запускает загрузку состояния индекса (<see cref="Initialization"/>).
    /// </summary>
    public MainViewModel(HomeViewModel home, QuickSearchViewModel search, SettingsViewModel settings, ISearchService searchService)
        : this(home, search, settings, searchService, static () => DateTimeOffset.Now)
    {
    }

    /// <summary>
    /// Создаёт оболочку с заданными часами (для тестов и превью).
    /// </summary>
    public MainViewModel(HomeViewModel home, QuickSearchViewModel search, SettingsViewModel settings, ISearchService searchService, Func<DateTimeOffset> clock)
    {
        ArgumentNullException.ThrowIfNull(home);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(searchService);
        ArgumentNullException.ThrowIfNull(clock);

        _search = searchService;
        _clock = clock;

        Pages =
        [
            home,
            search,
            new PlaceholderPageViewModel(
                PageKeys.Browse, "Обзор", FASymbol.Folder, Symbol.FolderOpen,
                "Дерево папок, список и плитка, адресная строка и панель сведений — как в Проводнике, только быстрее и с поиском под рукой.",
                "Этап 8"),
            new PlaceholderPageViewModel(
                PageKeys.Collections, "Коллекции", FASymbol.Library, Symbol.Collections,
                "Избранное, закреплённые папки и подборки файлов из разных мест — для проектов, поездок и дел.",
                "Этап 8"),
            new PlaceholderPageViewModel(
                PageKeys.Tags, "Теги", FASymbol.Tag, Symbol.TagMultiple,
                "Теги и цвета для файлов и папок; фильтр tag: в поиске; быстрые метки одной клавишей.",
                "Этап 8"),
            new PlaceholderPageViewModel(
                PageKeys.Inbox, "Входящие", FASymbol.Download, Symbol.MailInbox,
                "Разбор «Загрузок» по профильным складам: подсказка «куда положить», одна клавиша — следующий файл, отмена любого решения.",
                "Этап 17"),
            new PlaceholderPageViewModel(
                PageKeys.Operations, "Операции", FASymbol.Sync, Symbol.ArrowSync,
                "Очередь копирования, перемещения и резервных копий с прогрессом, отменой и журналом.",
                "Этап 8"),
        ];
        FooterPages = [settings];
        SelectedPage = home;

        NoticeTitle = string.Empty;
        NoticeMessage = string.Empty;
        NoticeSeverity = FAInfoBarSeverity.Informational;
        IndexStatusText = string.Empty;
        IndexStatusShort = string.Empty;

        Initialization = RefreshAsync();
    }

    /// <summary>Завершается, когда состояние индекса загружено.</summary>
    public Task Initialization { get; }

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

    /// <summary>
    /// Переходит на страницу по ключу.
    /// </summary>
    /// <returns><c>true</c>, если страница найдена.</returns>
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
        NoticeTitle = title;
        NoticeMessage = message;
        NoticeSeverity = severity;
        IsNoticeOpen = true;
    }

    /// <summary>
    /// Перечитывает состояние индекса: подписи и счётчик входящих.
    /// </summary>
    public async Task RefreshAsync()
    {
        var status = await _search.GetIndexStatusAsync(CancellationToken.None);

        IsIndexing = status.IsIndexing;
        IndexStatusText = status.Describe(_clock());
        IndexStatusShort = status.IsIndexing ? $"Индексация {Text.Formatting.Percent(status.Progress)}" : "Индекс актуален";

        if (Pages.FirstOrDefault(page => page.Key == PageKeys.Inbox) is { } inbox)
        {
            inbox.BadgeValue = status.InboxCount;
        }
    }
}

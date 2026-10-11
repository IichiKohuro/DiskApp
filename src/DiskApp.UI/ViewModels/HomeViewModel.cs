using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.Text;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.ViewModels;

/// <summary>
/// Главная страница: приветствие, состояние индекса, быстрые действия, недавние файлы и напоминание о входящих.
/// </summary>
public sealed partial class HomeViewModel : PageViewModel
{
    private readonly ISearchService _search;
    private readonly IAppCommands _commands;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>
    /// Создаёт страницу и сразу запускает загрузку данных (<see cref="Initialization"/>).
    /// </summary>
    public HomeViewModel(ISearchService search, IAppCommands commands, HotkeyStatus hotkeys)
        : this(search, commands, hotkeys, static () => DateTimeOffset.Now)
    {
    }

    /// <summary>
    /// Создаёт страницу с заданными часами (для тестов и превью).
    /// </summary>
    public HomeViewModel(ISearchService search, IAppCommands commands, HotkeyStatus hotkeys, Func<DateTimeOffset> clock)
        : base(PageKeys.Home, "Главная", FASymbol.Home)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(hotkeys);
        ArgumentNullException.ThrowIfNull(clock);

        _search = search;
        _commands = commands;
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

    /// <summary>
    /// Перечитывает состояние индекса и список недавних файлов.
    /// </summary>
    public async Task RefreshAsync()
    {
        var status = await _search.GetIndexStatusAsync(CancellationToken.None);
        var recent = await _search.GetRecentAsync(CancellationToken.None);
        var now = _clock();

        IndexStatusText = status.Describe(now);
        IsIndexing = status.IsIndexing;
        IndexProgressPercent = status.Progress * 100;
        InboxCount = status.InboxCount;
        InboxText = status.InboxCount > 0
            ? $"{Formatting.Count(status.InboxCount, "файл ждёт", "файла ждут", "файлов ждут")} разбора в «Загрузках»"
            : "Во «входящих» пусто — всё разобрано";

        RecentFiles.Clear();
        foreach (var entry in recent)
        {
            RecentFiles.Add(new SearchHit(entry, string.Empty, 0, now));
        }
    }

    /// <summary>
    /// Приветствие по часу суток: утро с 5 до 11, день до 17, вечер до 22, ночь — остальное.
    /// </summary>
    public static string GreetingFor(int hour) => hour switch
    {
        >= 5 and < 12 => "Доброе утро",
        >= 12 and < 18 => "Добрый день",
        >= 18 and < 23 => "Добрый вечер",
        _ => "Доброй ночи",
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

        await _search.OpenAsync(hit.Entry, null, CancellationToken.None);
        LastOpenedText = $"Открыт «{hit.Entry.Name}» — в прототипе файл не запускается, это появится в этапе 7.3";
        await RefreshAsync();
    }
}

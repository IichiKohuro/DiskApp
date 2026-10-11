using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DiskApp.UI.ViewModels;

/// <summary>Состояние индекса для точки в панели навигации главного окна.</summary>
public enum IndexHealth
{
    /// <summary>Сервер ещё запускается.</summary>
    Starting,

    /// <summary>Индекс актуален.</summary>
    Idle,

    /// <summary>Идёт индексация.</summary>
    Indexing,

    /// <summary>Индексация на паузе.</summary>
    Paused,

    /// <summary>Часть корней недоступна.</summary>
    Offline,

    /// <summary>Ошибка индексации или сервер не запустился.</summary>
    Error,
}

/// <summary>
/// Оболочка главного окна (PLAN.md, этап 8.1): поиск в заголовке окна и состояние индекса по событиям сервера.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Текст поля поиска в заголовке окна.</summary>
    [ObservableProperty]
    public partial string HeaderQuery { get; set; } = string.Empty;

    /// <summary>Состояние индекса.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHealthIndexing), nameof(IsHealthWarning), nameof(IsHealthError), nameof(IsHealthStarting))]
    public partial IndexHealth Health { get; set; } = IndexHealth.Idle;

    /// <summary>Подробности состояния для подсказки: какие корни недоступны, что индексируется.</summary>
    [ObservableProperty]
    public partial string IndexStatusDetail { get; set; } = string.Empty;

    /// <summary>Полоса о состоянии индекса открыта: корни недоступны или индексация упала.</summary>
    [ObservableProperty]
    public partial bool IsIndexBarOpen { get; set; }

    /// <summary>Заголовок полосы о состоянии индекса.</summary>
    [ObservableProperty]
    public partial string IndexBarTitle { get; set; } = string.Empty;

    /// <summary>Вид полосы о состоянии индекса.</summary>
    [ObservableProperty]
    public partial FluentAvalonia.UI.Controls.FAInfoBarSeverity IndexBarSeverity { get; set; }

    /// <summary>Идёт индексация — точка акцентного цвета.</summary>
    public bool IsHealthIndexing => Health == IndexHealth.Indexing;

    /// <summary>Пауза или недоступные корни — жёлтая точка.</summary>
    public bool IsHealthWarning => Health is IndexHealth.Paused or IndexHealth.Offline;

    /// <summary>Ошибка — красная точка.</summary>
    public bool IsHealthError => Health == IndexHealth.Error;

    /// <summary>Сервер запускается — серая точка.</summary>
    public bool IsHealthStarting => Health == IndexHealth.Starting;

    /// <summary>
    /// Применяет состояние индекса, пришедшее от сервера (оболочка получает его по событиям хаба).
    /// </summary>
    /// <param name="health">Состояние.</param>
    /// <param name="shortText">Короткая подпись под навигацией: «Индекс актуален», «Индексация · 12 345».</param>
    /// <param name="detail">Подробности для подсказки.</param>
    public void ApplyIndexHealth(IndexHealth health, string shortText, string detail)
    {
        ArgumentNullException.ThrowIfNull(shortText);
        ArgumentNullException.ThrowIfNull(detail);

        var wasIndexing = IsIndexing;
        _healthFromServer = true;
        var changed = Health != health;
        Health = health;
        IsIndexing = health == IndexHealth.Indexing;
        IndexStatusShort = shortText;
        IndexStatusDetail = detail;

        // Недоступные корни и ошибки видны полосой над страницей; закрытая пользователем полоса снова появится при смене состояния.
        if (health is IndexHealth.Offline or IndexHealth.Error)
        {
            var title = health == IndexHealth.Offline ? "Часть корней недоступна" : "Ошибка индексации";
            if (!IsIndexBarOpen || IndexBarTitle != title)
            {
                IndexBarTitle = title;
                IndexBarSeverity = health == IndexHealth.Offline ? FluentAvalonia.UI.Controls.FAInfoBarSeverity.Warning : FluentAvalonia.UI.Controls.FAInfoBarSeverity.Error;
                IsIndexBarOpen = true;
            }
        }
        else
        {
            IsIndexBarOpen = false;
        }

        // Состояние корней сменилось: карточки на главной перечитываются.
        if (changed && Pages.OfType<HomeViewModel>().FirstOrDefault() is { } home)
        {
            _ = home.RefreshRootsAsync();
        }

        // Индексация закончилась: подписи и счётчики страниц перечитываются.
        if (wasIndexing && !IsIndexing)
        {
            _ = RefreshAsync();
        }
    }

    /// <summary>
    /// Поиск из заголовка: открывает страницу «Поиск» с введённым текстом.
    /// </summary>
    [RelayCommand]
    private void SearchFromHeader()
    {
        var query = HeaderQuery.Trim();
        NavigateTo(PageKeys.Search);
        _quickSearch.Query = query;
        HeaderQuery = string.Empty;
    }
}

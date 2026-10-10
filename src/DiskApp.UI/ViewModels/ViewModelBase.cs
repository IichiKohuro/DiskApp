using CommunityToolkit.Mvvm.ComponentModel;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.ViewModels;

/// <summary>
/// Базовый класс всех ViewModel: уведомления об изменении свойств из CommunityToolkit.Mvvm.
/// </summary>
public abstract class ViewModelBase : ObservableObject
{
}

/// <summary>
/// Ключи страниц главного окна для навигации по имени (трей, аргументы командной строки, команды оболочки).
/// </summary>
public static class PageKeys
{
    /// <summary>Главная.</summary>
    public const string Home = "home";

    /// <summary>Поиск.</summary>
    public const string Search = "search";

    /// <summary>Обзор.</summary>
    public const string Browse = "browse";

    /// <summary>Коллекции.</summary>
    public const string Collections = "collections";

    /// <summary>Теги.</summary>
    public const string Tags = "tags";

    /// <summary>Входящие.</summary>
    public const string Inbox = "inbox";

    /// <summary>Операции.</summary>
    public const string Operations = "operations";

    /// <summary>Настройки.</summary>
    public const string Settings = "settings";
}

/// <summary>
/// Страница главного окна: пункт навигации с заголовком, иконкой и необязательным счётчиком.
/// </summary>
public abstract partial class PageViewModel : ViewModelBase
{
    /// <summary>
    /// Создаёт страницу.
    /// </summary>
    /// <param name="key">Ключ из <see cref="PageKeys"/>.</param>
    /// <param name="title">Заголовок в навигации.</param>
    /// <param name="icon">Иконка пункта навигации.</param>
    protected PageViewModel(string key, string title, FASymbol icon)
    {
        Key = key;
        Title = title;
        Icon = icon;
    }

    /// <summary>Ключ страницы.</summary>
    public string Key { get; }

    /// <summary>Заголовок в навигации.</summary>
    public string Title { get; }

    /// <summary>Иконка пункта навигации.</summary>
    public FASymbol Icon { get; }

    /// <summary>Счётчик на пункте навигации, например число входящих; 0 — без счётчика.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadge))]
    public partial int BadgeValue { get; set; }

    /// <summary>Показывать ли счётчик.</summary>
    public bool HasBadge => BadgeValue > 0;
}

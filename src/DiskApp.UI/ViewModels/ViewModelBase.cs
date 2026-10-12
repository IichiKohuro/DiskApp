using CommunityToolkit.Mvvm.ComponentModel;

using DiskApp.UI.Localization;

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

    /// <summary>Заметки (этап 23).</summary>
    public const string Notes = "notes";

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
    /// <param name="title">Ключ строки заголовка в навигации (или готовый текст).</param>
    /// <param name="icon">Иконка пункта навигации.</param>
    protected PageViewModel(string key, string title, FASymbol icon)
    {
        Key = key;
        TitleKey = title;
        Icon = icon;

        // При смене языка заголовок и собранные в коде тексты перечитываются (этап 9.2). Подписка слабая: страница из
        // тестов или превью не держится в памяти событием.
        var page = new WeakReference<PageViewModel>(this);
        EventHandler? handler = null;
        handler = (_, _) =>
        {
            if (page.TryGetTarget(out var target))
            {
                target.OnLanguageChanged();
            }
            else
            {
                Localizer.Instance.LanguageChanged -= handler;
            }
        };
        Localizer.Instance.LanguageChanged += handler;
    }

    /// <summary>
    /// Перечитывает тексты, собранные в коде, после смены языка: по умолчанию ничего, страницы со сводками
    /// перезагружают данные.
    /// </summary>
    protected virtual Task RelocalizeAsync() => Task.CompletedTask;

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(Title));
        _ = RelocalizeSafelyAsync();
    }

    private async Task RelocalizeSafelyAsync()
    {
        try
        {
            await RelocalizeAsync();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Тексты обновятся при следующем показе страницы.
            _ = exception;
        }
    }

    /// <summary>Ключ страницы.</summary>
    public string Key { get; }

    /// <summary>Ключ строки заголовка.</summary>
    public string TitleKey { get; }

    /// <summary>Заголовок в навигации на языке интерфейса.</summary>
    public string Title => L.T(TitleKey);

    /// <summary>Иконка пункта навигации.</summary>
    public FASymbol Icon { get; }

    /// <summary>Счётчик на пункте навигации, например число входящих; 0 — без счётчика.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadge))]
    public partial int BadgeValue { get; set; }

    /// <summary>Показывать ли счётчик.</summary>
    public bool HasBadge => BadgeValue > 0;
}

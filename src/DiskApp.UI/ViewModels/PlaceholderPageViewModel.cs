using FluentAvalonia.UI.Controls;

using FluentIcons.Common;

namespace DiskApp.UI.ViewModels;

/// <summary>
/// Страница, которой пока нет: показывает, что здесь появится и в каком этапе плана.
/// Заменяется настоящей страницей по мере выполнения этапов 8 и 17.
/// </summary>
public sealed class PlaceholderPageViewModel : PageViewModel
{
    /// <summary>
    /// Создаёт заглушку страницы.
    /// </summary>
    /// <param name="key">Ключ из <see cref="PageKeys"/>.</param>
    /// <param name="title">Заголовок.</param>
    /// <param name="icon">Иконка пункта навигации.</param>
    /// <param name="illustration">Крупная иконка в центре страницы.</param>
    /// <param name="description">Что будет на странице, одним-двумя предложениями.</param>
    /// <param name="stageText">Этап плана, в котором страница появится, например «Этап 8».</param>
    /// <param name="badge">Счётчик на пункте навигации; 0 — без счётчика.</param>
    public PlaceholderPageViewModel(string key, string title, FASymbol icon, Symbol illustration, string description, string stageText, int badge = 0)
        : base(key, title, icon)
    {
        Illustration = illustration;
        Description = description;
        StageText = stageText;
        BadgeValue = badge;
    }

    /// <summary>Крупная иконка в центре страницы.</summary>
    public Symbol Illustration { get; }

    /// <summary>Что будет на странице.</summary>
    public string Description { get; }

    /// <summary>Этап плана, в котором страница появится.</summary>
    public string StageText { get; }
}

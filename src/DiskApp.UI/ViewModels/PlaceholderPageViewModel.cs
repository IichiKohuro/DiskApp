using DiskApp.UI.Localization;

using FluentAvalonia.UI.Controls;

using FluentIcons.Common;

namespace DiskApp.UI.ViewModels;

/// <summary>
/// Страница, которой пока нет: показывает, что здесь появится и в каком этапе плана.
/// Заменяется настоящей страницей по мере выполнения этапов 8 и 17.
/// </summary>
public sealed class PlaceholderPageViewModel : PageViewModel
{
    private readonly string _description;
    private readonly string _stageText;

    /// <summary>
    /// Создаёт заглушку страницы.
    /// </summary>
    /// <param name="key">Ключ из <see cref="PageKeys"/>.</param>
    /// <param name="title">Ключ строки заголовка или готовый текст.</param>
    /// <param name="icon">Иконка пункта навигации.</param>
    /// <param name="illustration">Крупная иконка в центре страницы.</param>
    /// <param name="description">Ключ строки: что будет на странице, одним-двумя предложениями.</param>
    /// <param name="stageText">Ключ строки: этап плана, в котором страница появится, например «Этап 8».</param>
    /// <param name="badge">Счётчик на пункте навигации; 0 — без счётчика.</param>
    public PlaceholderPageViewModel(string key, string title, FASymbol icon, Symbol illustration, string description, string stageText, int badge = 0)
        : base(key, title, icon)
    {
        Illustration = illustration;
        _description = description;
        _stageText = stageText;
        BadgeValue = badge;
    }

    /// <summary>Крупная иконка в центре страницы.</summary>
    public Symbol Illustration { get; }

    /// <summary>Что будет на странице.</summary>
    public string Description => L.T(_description);

    /// <summary>Этап плана, в котором страница появится.</summary>
    public string StageText => L.T(_stageText);
}

namespace DiskApp.UI.ViewModels;

/// <summary>
/// Что показывают строки быстрого поиска (PLAN.md, этап 9.1): папку, размер, дату и подборку при пустом запросе.
/// </summary>
/// <param name="ShowPath">Папка.</param>
/// <param name="ShowSize">Размер.</param>
/// <param name="ShowDate">Дата изменения.</param>
/// <param name="ShowSuggestions">Недавние, закреплённые и изменённые файлы при пустом запросе.</param>
public sealed record SearchDisplay(bool ShowPath = true, bool ShowSize = true, bool ShowDate = true, bool ShowSuggestions = true)
{
    /// <summary>Показывать всё.</summary>
    public static SearchDisplay Default { get; } = new();
}

/// <summary>
/// Настройки отображения окна быстрого поиска.
/// </summary>
public sealed partial class QuickSearchViewModel
{
    private SearchDisplay _display = SearchDisplay.Default;

    /// <summary>Что показывают строки; изменение перестраивает список.</summary>
    public SearchDisplay Display
    {
        get => _display;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value == _display)
            {
                return;
            }

            _display = value;
            PendingRefresh = RefreshAsync();
        }
    }
}

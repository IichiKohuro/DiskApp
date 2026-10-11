using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DiskApp.UI.Localization;
using DiskApp.UI.Models;
using DiskApp.UI.Text;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.ViewModels;

/// <summary>
/// Фильтры и подсказки быстрого поиска (PLAN.md, этап 7.5): чипы вида, даты и корня меняют текст запроса,
/// подсказки дополняют фильтр (<c>ext:p</c> → <c>ext:pdf</c>) или предлагают прошлые запросы с удалением,
/// запрос <c>?</c> показывает синтаксис и клавиши.
/// </summary>
public sealed partial class QuickSearchViewModel
{
    private const string HelpQuery = "?";

    /// <summary>Чипы фильтров: вид файла, дата изменения, корни (если их больше одного).</summary>
    public ObservableCollection<FilterChip> FilterChips { get; } = CreateFilterChips();

    /// <summary>Подсказки под полем: дополнения фильтра или прошлые запросы.</summary>
    public ObservableCollection<QuerySuggestion> Suggestions { get; } = [];

    /// <summary>Видны ли чипы фильтров; переключает <c>Ctrl+F</c>.</summary>
    [ObservableProperty]
    public partial bool ShowFilters { get; set; } = true;

    /// <summary>Видны ли подсказки.</summary>
    [ObservableProperty]
    public partial bool ShowSuggestions { get; set; }

    /// <summary>Подсказки — это история запросов (при пустом поле): их можно удалять.</summary>
    [ObservableProperty]
    public partial bool IsShowingHistory { get; set; }

    /// <summary>Подпись строки подсказок: «История» или «Подсказки».</summary>
    [ObservableProperty]
    public partial string SuggestionsTitle { get; set; } = string.Empty;

    /// <summary>Показана подсказка синтаксиса (запрос <c>?</c>).</summary>
    [ObservableProperty]
    public partial bool ShowHelp { get; set; }

    /// <summary>
    /// Текст запроса заменён программно (чип, подсказка, история): представлению поставить курсор в конец.
    /// </summary>
    public event EventHandler? QueryReplaced;

    /// <summary>Включает фильтр чипа или выключает его; остальные фильтры той же группы убираются.</summary>
    [RelayCommand]
    private void ToggleFilter(FilterChip chip)
    {
        ArgumentNullException.ThrowIfNull(chip);
        ReplaceQuery(QueryText.WithFilter(Query, chip.Key, chip.IsActive ? null : chip.Token));
    }

    /// <summary>Применяет подсказку: после значения фильтра — пробел, чтобы продолжить ввод.</summary>
    [RelayCommand]
    private void ApplySuggestion(QuerySuggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        ReplaceQuery(suggestion.Kind == QuerySuggestionKind.FilterValue ? suggestion.Query + " " : suggestion.Query);
    }

    /// <summary>Удаляет запрос из истории.</summary>
    [RelayCommand]
    private async Task DeleteHistoryAsync(QuerySuggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        if (await TryAsync(() => _search.DeleteHistoryAsync(suggestion.Query, CancellationToken.None), L.T("QuickSearch.CouldNotRemoveFromHistory")))
        {
            Suggestions.Remove(suggestion);
            ShowSuggestions = Suggestions.Count > 0;
            _history = null;
        }
    }

    /// <summary>Очищает историю запросов.</summary>
    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        if (await TryAsync(() => _search.ClearHistoryAsync(CancellationToken.None), L.T("QuickSearch.CouldNotClearHistory")))
        {
            Suggestions.Clear();
            ShowSuggestions = false;
            _history = null;
            ShowNotice(L.T("QuickSearch.HistoryCleared"), L.T("QuickSearch.PreviousQueriesAreNoLongerSuggested"), FAInfoBarSeverity.Success);
        }
    }

    /// <summary>
    /// Подсказка о фильтрах открыта (PLAN.md, этап 9.3). Она встроена в окно, а не всплывает отдельным окном: всплывающее
    /// окно забирало бы фокус, и окно поиска пряталось бы при потере фокуса.
    /// </summary>
    [ObservableProperty]
    public partial bool IsFiltersTipOpen { get; set; }

    /// <summary>
    /// Показать подсказку о фильтрах, если она ещё не показывалась; вызывает окно после показа. Подсказка считается
    /// показанной сразу и закрывается кнопкой или вводом запроса.
    /// </summary>
    public void ShowTips()
    {
        if (!IsFiltersTipOpen && _tips.ShouldShow(Services.Tips.Filters))
        {
            IsFiltersTipOpen = true;
            _tips.MarkShown(Services.Tips.Filters);
        }
    }

    /// <summary>Закрыть подсказку о фильтрах.</summary>
    [RelayCommand]
    private void DismissFiltersTip() => IsFiltersTipOpen = false;

    /// <summary>Показывает или прячет чипы фильтров.</summary>
    [RelayCommand]
    private void ToggleFilters() => ShowFilters = !ShowFilters;

    private static ObservableCollection<FilterChip> CreateFilterChips() =>
    [
        new(L.T("QuickSearch.Documents"), "type:document", FilterGroup.Kind),
        new("PDF", "type:pdf", FilterGroup.Kind),
        new(L.T("QuickSearch.Images"), "type:image", FilterGroup.Kind),
        new(L.T("QuickSearch.Videos"), "type:video", FilterGroup.Kind),
        new(L.T("QuickSearch.Audio"), "type:audio", FilterGroup.Kind),
        new(L.T("QuickSearch.Archives"), "type:archive", FilterGroup.Kind),
        new(L.T("QuickSearch.Code"), "type:code", FilterGroup.Kind),
        new(L.T("QuickSearch.Folders"), "type:folder", FilterGroup.Kind),
        new(L.T("QuickSearch.Today"), "modified:today", FilterGroup.Date),
        new(L.T("QuickSearch.Week"), "modified:week", FilterGroup.Date),
        new(L.T("QuickSearch.Month"), "modified:month", FilterGroup.Date),
        new(L.T("QuickSearch.Year"), "modified:year", FilterGroup.Date),
    ];

    private void ReplaceQuery(string query)
    {
        Query = query;
        QueryReplaced?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateFilterChips()
    {
        foreach (var chip in FilterChips)
        {
            chip.IsActive = QueryText.Contains(Query, chip.Token);
        }
    }

    // Чипы корней появляются, когда корней больше одного: с одним корнем фильтр ничего не меняет.
    private void UpdateRootChips(IndexStatus status)
    {
        var labels = status.Roots.Count > 1 ? status.Roots : [];
        var current = FilterChips.Where(static chip => chip.Group == FilterGroup.Root).ToList();
        if (current.Select(static chip => chip.Label).SequenceEqual(labels))
        {
            return;
        }

        foreach (var chip in current)
        {
            FilterChips.Remove(chip);
        }

        foreach (var label in labels)
        {
            FilterChips.Add(new FilterChip(label, "root:" + QueryText.Quote(label), FilterGroup.Root));
        }

        UpdateFilterChips();
    }

    private async Task<IReadOnlyList<QuerySuggestion>> LoadSuggestionsAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            var all = await _search.SuggestAsync(query, cancellationToken);

            // При пустом поле — история; при вводе — только дополнения фильтров: история слов отвлекала бы.
            var history = query.Trim().Length == 0;
            return [.. all.Where(suggestion => suggestion.IsHistory == history)];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return [];
        }
    }

    private void ShowSuggestionList(string query, IReadOnlyList<QuerySuggestion> suggestions)
    {
        Suggestions.Clear();
        foreach (var suggestion in suggestions)
        {
            Suggestions.Add(suggestion);
        }

        IsShowingHistory = query.Trim().Length == 0;
        SuggestionsTitle = IsShowingHistory ? L.T("QuickSearch.History") : L.T("QuickSearch.Suggestions");
        ShowSuggestions = Suggestions.Count > 0;
    }

    private void ShowHelpPanel()
    {
        HasError = false;
        Results.Clear();
        Rows.Clear();
        SelectedIndex = -1;
        NotifySelectionChanged();
        Suggestions.Clear();
        ShowSuggestions = false;
        ShowResults = false;
        ShowEmptyState = false;
        ShowHelp = true;
        StatusText = L.T("QuickSearch.QuerySyntaxAndKeysEscCloses");
    }
}

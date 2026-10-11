using DiskApp.UI.Localization;
using DiskApp.UI.Models;

namespace DiskApp.UI.Input;

/// <summary>
/// Подсказка синтаксиса запроса и клавиш, которую показывает запрос <c>?</c> (PLAN.md, этап 7.5).
/// Фильтры совпадают с разбором на сервере (<c>QueryParser</c>, <c>FilterValues</c>), клавиши — с <see cref="QuickSearchKeyMap"/>.
/// </summary>
public static class SearchHelp
{
    /// <summary>Строки подсказки по порядку: заголовки разделов и примеры.</summary>
    public static IReadOnlyList<HelpItem> Items { get; } =
    [
        new(string.Empty, L.T("SearchHelp.Query"), IsHeader: true),
        new(L.T("SearchHelp.Report2026"), L.T("SearchHelp.AllWordsInTheNameIn")),
        new(L.T("SearchHelp.AnnualReport"), L.T("SearchHelp.ExactPhrase")),
        new(L.T("SearchHelp.Draft"), L.T("SearchHelp.ExcludeAWordWorksWithFilters")),
        new(string.Empty, L.T("SearchHelp.Filters"), IsHeader: true),
        new("ext:pdf  ext:jpg,png", L.T("SearchHelp.Extension")),
        new("type:image", L.T("SearchHelp.KindDocumentPdfImageVideoAudio")),
        new(L.T("SearchHelp.InProjects"), L.T("SearchHelp.InAFolderWithThisName")),
        new(L.T("SearchHelp.RootDocuments"), L.T("SearchHelp.InAnIndexRoot")),
        new("modified:week", L.T("SearchHelp.ModifiedTodayYesterdayWeekMonthYear")),
        new("created:2026", L.T("SearchHelp.CreatedSameAsModified")),
        new("size:>10mb", L.T("SearchHelp.Size10mb1g500kb1mb5mb")),
        new(L.T("SearchHelp.TagWorkFavYes"), L.T("SearchHelp.TagFavorites")),
        new("is:dir  is:hidden", L.T("SearchHelp.FoldersHiddenCloudIsCloud")),
        new(string.Empty, L.T("SearchHelp.Keys"), IsHeader: true),
        new("Enter", L.T("SearchHelp.OpenCtrl19OpensThe")),
        new(QuickSearchKeyMap.RevealText, L.T("SearchHelp.ShowInFolder")),
        new(QuickSearchKeyMap.OpenWithText, L.T("SearchHelp.OpenWith")),
        new(QuickSearchKeyMap.PropertiesText, L.T("SearchHelp.Properties")),
        new(QuickSearchKeyMap.CopyFilesText + " / " + QuickSearchKeyMap.CopyPathText, L.T("SearchHelp.CopyFilePath")),
        new(QuickSearchKeyMap.RenameText + " / " + QuickSearchKeyMap.DeleteText, L.T("SearchHelp.RenameToRecycleBin")),
        new(QuickSearchKeyMap.TogglePinText, L.T("SearchHelp.Pin")),
        new("Tab", L.T("SearchHelp.CompleteTheFilterFromTheHint")),
        new("Ctrl+F", L.T("SearchHelp.ShowOrHideFilterChips")),
        new(L.T("SearchHelp.InAnEmptyBox"), L.T("SearchHelp.PreviousQueries")),
        new("Esc", L.T("SearchHelp.ClearTheQueryThenCloseThe")),
    ];
}

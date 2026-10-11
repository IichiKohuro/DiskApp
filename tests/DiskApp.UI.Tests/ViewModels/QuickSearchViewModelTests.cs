using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Быстрый поиск поверх макетного сервиса: недавние при пустом запросе, подстрока без учёта регистра,
/// все слова запроса, клавиатурная навигация, Esc и открытие файла.
/// </summary>
public sealed class QuickSearchViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 15, 0, 0, TimeSpan.FromHours(3));

    [Fact]
    public async Task Constructor_EmptyQuery_ShowsSectionsWithoutDuplicates()
    {
        // Arrange & Act
        using var viewModel = Create();
        await viewModel.PendingRefresh;

        // Assert
        viewModel.Rows.OfType<ResultSectionHeader>().Select(static header => header.Title)
            .ShouldBe(["Недавно открытые", "Закреплённые", "Недавно изменённые"]);
        viewModel.Rows[0].ShouldBeOfType<ResultSectionHeader>();
        viewModel.Results[0].Entry.Name.ShouldBe("MainView.axaml");
        viewModel.Results.Select(static hit => hit.Entry.FullPath).ShouldBeUnique("запись показывается только в первой секции");
        viewModel.Results.Count(static hit => hit.Entry.Name == "Бюджет 2026.xlsx").ShouldBe(1);
        viewModel.Results.Where(static hit => hit.HasNumber).Select(static hit => hit.Number).ShouldBe([1, 2, 3, 4, 5, 6, 7, 8, 9]);
        viewModel.SelectedIndex.ShouldBe(0);
        viewModel.SelectedRow.ShouldBeSameAs(viewModel.Results[0]);
        viewModel.ShowResults.ShouldBeTrue();
        viewModel.ShowEmptyState.ShouldBeFalse();
        viewModel.StatusText.ShouldContain("в индексе");
    }

    [Fact]
    public async Task SelectedRow_HeaderOrNull_KeepsSelection()
    {
        // Arrange
        using var viewModel = Create();
        await viewModel.PendingRefresh;
        viewModel.MoveSelection(2);

        // Act
        viewModel.SelectedRow = viewModel.Rows.OfType<ResultSectionHeader>().First();
        viewModel.SelectedRow = null;

        // Assert
        viewModel.SelectedIndex.ShouldBe(2);
    }

    [Fact]
    public async Task SelectedRow_Hit_SelectsItsResultIndex()
    {
        // Arrange
        using var viewModel = Create();
        await viewModel.PendingRefresh;
        var target = viewModel.Results[7];

        // Act
        viewModel.SelectedRow = target;

        // Assert
        viewModel.SelectedIndex.ShouldBe(7);
        viewModel.SelectedResult.ShouldBeSameAs(target);
    }

    [Fact]
    public async Task Query_Substring_FindsMatchesCaseInsensitivelyAndHighlights()
    {
        // Arrange
        using var viewModel = Create();

        // Act
        viewModel.Query = "ОТЧЁТ";
        await viewModel.PendingRefresh;

        // Assert
        viewModel.Results.Count.ShouldBe(2);
        viewModel.Results.ShouldAllBe(hit => hit.Entry.Name.Contains("Отчёт", StringComparison.Ordinal));
        viewModel.Results[0].Entry.Name.ShouldBe("Отчёт за 3 квартал 2026.docx", "свежий файл выше старого");
        viewModel.Results[0].Highlight.ShouldBe("ОТЧЁТ");
        viewModel.Results[0].Number.ShouldBe(1);
        viewModel.Rows.ShouldNotContain(row => row is ResultSectionHeader, "у результатов поиска нет секций");
        viewModel.StatusText.ShouldStartWith("2 результата");
    }

    [Fact]
    public async Task Query_MultipleWords_RequiresEveryWord()
    {
        // Arrange
        using var viewModel = Create();

        // Act
        viewModel.Query = "квартал 2026";
        await viewModel.PendingRefresh;

        // Assert
        viewModel.Results.Select(hit => hit.Entry.Name).ShouldBe(
            ["Отчёт за 3 квартал 2026.docx", "Отчёт за 2 квартал 2026.docx"]);
    }

    [Fact]
    public async Task Query_NoMatches_ShowsEmptyState()
    {
        // Arrange
        using var viewModel = Create();

        // Act
        viewModel.Query = "zzzz-ничего-такого";
        await viewModel.PendingRefresh;

        // Assert
        viewModel.Results.ShouldBeEmpty();
        viewModel.ShowEmptyState.ShouldBeTrue();
        viewModel.ShowResults.ShouldBeFalse();
        viewModel.SelectedIndex.ShouldBe(-1);
        viewModel.SelectedResult.ShouldBeNull();
        viewModel.StatusText.ShouldStartWith("Ничего не найдено");
    }

    [Fact]
    public async Task Query_RapidTyping_KeepsOnlyLatestResults()
    {
        // Arrange
        using var viewModel = Create();
        viewModel.DebounceDelay = TimeSpan.FromMilliseconds(20);

        // Act
        viewModel.Query = "от";
        viewModel.Query = "отчёт за 3";
        await viewModel.PendingRefresh;

        // Assert
        viewModel.Results.Count.ShouldBe(1);
        viewModel.Results[0].Entry.Name.ShouldBe("Отчёт за 3 квартал 2026.docx");
    }

    [Fact]
    public async Task MoveSelection_PastEnds_ClampsToBounds()
    {
        // Arrange
        using var viewModel = Create();
        await viewModel.PendingRefresh;

        // Act & Assert
        viewModel.MoveSelection(-5);
        viewModel.SelectedIndex.ShouldBe(0);

        viewModel.MoveSelection(100);
        viewModel.SelectedIndex.ShouldBe(viewModel.Results.Count - 1);

        viewModel.MoveSelection(-1);
        viewModel.SelectedIndex.ShouldBe(viewModel.Results.Count - 2);
        viewModel.SelectedResult.ShouldBe(viewModel.Results[viewModel.Results.Count - 2]);
    }

    [Fact]
    public async Task SelectNumber_ExistingAndMissingNumbers_SelectsOrReportsFalse()
    {
        // Arrange
        using var viewModel = Create();
        await viewModel.PendingRefresh;

        // Act & Assert
        viewModel.SelectNumber(3).ShouldBeTrue();
        viewModel.SelectedIndex.ShouldBe(2);
        viewModel.Query = "ОТЧЁТ";
        await viewModel.PendingRefresh;
        viewModel.SelectNumber(2).ShouldBeTrue();
        viewModel.SelectedIndex.ShouldBe(1);
        viewModel.SelectNumber(3).ShouldBeFalse("найдено всего два файла");
        viewModel.SelectedIndex.ShouldBe(1);
    }

    [Fact]
    public async Task Escape_WithQuery_ClearsQueryWithoutClosing()
    {
        // Arrange
        using var viewModel = Create();
        var closeRequests = 0;
        viewModel.CloseRequested += (_, _) => closeRequests++;
        viewModel.Query = "pdf";
        await viewModel.PendingRefresh;

        // Act
        viewModel.Escape();
        await viewModel.PendingRefresh;

        // Assert
        viewModel.Query.ShouldBeEmpty();
        closeRequests.ShouldBe(0);
        viewModel.Rows[0].ShouldBe(new ResultSectionHeader("Недавно открытые"));
    }

    [Fact]
    public async Task Escape_WithEmptyQuery_RequestsClose()
    {
        // Arrange
        using var viewModel = Create();
        await viewModel.PendingRefresh;
        var closeRequests = 0;
        viewModel.CloseRequested += (_, _) => closeRequests++;

        // Act
        viewModel.Escape();

        // Assert
        closeRequests.ShouldBe(1);
    }

    [Fact]
    public async Task OpenSelectedAsync_WithSelection_RaisesFileOpenedAndPromotesToRecent()
    {
        // Arrange
        using var viewModel = Create();
        FileEntry? opened = null;
        viewModel.FileOpened += (_, entry) => opened = entry;
        viewModel.Query = "бюджет";
        await viewModel.PendingRefresh;

        // Act
        var result = await viewModel.OpenSelectedAsync();
        viewModel.Query = string.Empty;
        await viewModel.PendingRefresh;

        // Assert
        result.ShouldBeTrue();
        opened.ShouldNotBeNull();
        opened.Name.ShouldBe("Бюджет 2026.xlsx");
        viewModel.Results[0].Entry.Name.ShouldBe("Бюджет 2026.xlsx", "открытый файл поднимается в начало недавних");
    }

    [Fact]
    public async Task OpenSelectedAsync_WithoutSelection_ReturnsFalse()
    {
        // Arrange
        using var viewModel = Create();
        viewModel.Query = "zzzz-ничего";
        await viewModel.PendingRefresh;

        // Act
        var result = await viewModel.OpenSelectedAsync();

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public async Task Query_ServerFails_ShowsErrorAndRecoversOnNextQuery()
    {
        // Arrange
        var search = new FlakySearchService { FailSearch = true };
        using var viewModel = new QuickSearchViewModel(search, static () => Now) { DebounceDelay = TimeSpan.Zero };
        await viewModel.PendingRefresh;

        // Act
        viewModel.Query = "бюджет";
        await viewModel.PendingRefresh;
        var failedText = viewModel.StatusText;
        var failed = viewModel.HasError;
        search.FailSearch = false;
        viewModel.Query = "бюджет 2026";
        await viewModel.PendingRefresh;

        // Assert
        failed.ShouldBeTrue();
        failedText.ShouldContain("Нет связи с сервером поиска");
        viewModel.HasError.ShouldBeFalse();
        viewModel.Results.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task OpenSelectedAsync_ServerFails_ShowsErrorAndDoesNotRaiseFileOpened()
    {
        // Arrange
        var search = new FlakySearchService { FailOpen = true };
        using var viewModel = new QuickSearchViewModel(search, static () => Now) { DebounceDelay = TimeSpan.Zero };
        var raised = false;
        viewModel.FileOpened += (_, _) => raised = true;
        viewModel.Query = "бюджет";
        await viewModel.PendingRefresh;

        // Act
        var result = await viewModel.OpenSelectedAsync();

        // Assert
        result.ShouldBeFalse();
        raised.ShouldBeFalse();
        viewModel.HasError.ShouldBeTrue();
        viewModel.StatusText.ShouldContain("Не удалось открыть");
        search.LastOpenQuery.ShouldBeNull();
    }

    [Fact]
    public async Task RetryAsync_AfterServerError_RepeatsQuery()
    {
        // Arrange
        var search = new FlakySearchService { FailSearch = true };
        using var viewModel = new QuickSearchViewModel(search, static () => Now) { DebounceDelay = TimeSpan.Zero };
        viewModel.Query = "бюджет";
        await viewModel.PendingRefresh;
        search.FailSearch = false;

        // Act
        await viewModel.RetryCommand.ExecuteAsync(null);

        // Assert
        viewModel.HasError.ShouldBeFalse();
        viewModel.Results[0].Entry.Name.ShouldBe("Бюджет 2026.xlsx");
    }

    [Fact]
    public async Task StatusText_WhileIndexing_ShowsProgressForSuggestionsAndSearch()
    {
        // Arrange
        var search = new FlakySearchService
        {
            Status = new IndexStatus { TotalEntries = 500, Roots = ["D:"], IsIndexing = true, Scanned = 12_345 },
        };
        using var viewModel = new QuickSearchViewModel(search, static () => Now) { DebounceDelay = TimeSpan.Zero };
        await viewModel.PendingRefresh;
        var suggestionsText = viewModel.StatusText;

        // Act
        search.Status = search.Status with { Progress = 0.43 };
        viewModel.Query = "бюджет";
        await viewModel.PendingRefresh;

        // Assert
        suggestionsText.ShouldStartWith("индексация: просмотрено 12");
        suggestionsText.ShouldContain("500 файлов в индексе");
        viewModel.StatusText.ShouldEndWith("· индексация 43 %");
    }

    [Fact]
    public async Task OpenSelectedAsync_WithQuery_PassesTrimmedQueryToService()
    {
        // Arrange
        var search = new FlakySearchService();
        using var viewModel = new QuickSearchViewModel(search, static () => Now) { DebounceDelay = TimeSpan.Zero };
        viewModel.Query = "  бюджет ";
        await viewModel.PendingRefresh;

        // Act
        await viewModel.OpenSelectedAsync();

        // Assert
        search.LastOpenQuery.ShouldBe("бюджет");
    }

    private static QuickSearchViewModel Create()
        => new(new SampleSearchService(static () => Now), static () => Now) { DebounceDelay = TimeSpan.Zero };

    /// <summary>
    /// Макетный поиск, который по флагу падает, как недоступный сервер.
    /// </summary>
    private sealed class FlakySearchService : ISearchService
    {
        private readonly SampleSearchService _inner = new(static () => Now);

        public bool FailSearch { get; set; }

        public bool FailOpen { get; set; }

        public IndexStatus? Status { get; set; }

        public string? LastOpenQuery { get; private set; }

        public Task<SearchResult> SearchAsync(string query, CancellationToken cancellationToken)
            => FailSearch ? throw new HttpRequestException("сервер не отвечает") : _inner.SearchAsync(query, cancellationToken);

        public Task<IReadOnlyList<FileEntry>> GetRecentAsync(CancellationToken cancellationToken) => _inner.GetRecentAsync(cancellationToken);

        public Task<IReadOnlyList<ResultSection>> GetSuggestionsAsync(CancellationToken cancellationToken) => _inner.GetSuggestionsAsync(cancellationToken);

        public Task<IReadOnlyList<string>> GetHistoryAsync(CancellationToken cancellationToken) => _inner.GetHistoryAsync(cancellationToken);

        public Task<IndexStatus> GetIndexStatusAsync(CancellationToken cancellationToken)
            => Status is { } status ? Task.FromResult(status) : _inner.GetIndexStatusAsync(cancellationToken);

        public Task OpenAsync(FileEntry entry, string? query, CancellationToken cancellationToken)
        {
            if (FailOpen)
            {
                throw new HttpRequestException("сервер не отвечает");
            }

            LastOpenQuery = query;
            return _inner.OpenAsync(entry, query, cancellationToken);
        }
    }
}

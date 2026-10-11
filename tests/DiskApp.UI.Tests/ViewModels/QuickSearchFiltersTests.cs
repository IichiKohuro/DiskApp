using DiskApp.UI.Input;
using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.Text;
using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Фильтры и подсказки быстрого поиска (PLAN.md, этап 7.5): чипы меняют текст запроса и следуют за ним,
/// подсказки дополняют фильтр и показывают историю с удалением, запрос «?» показывает синтаксис.
/// </summary>
public sealed class QuickSearchFiltersTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 15, 0, 0, TimeSpan.FromHours(3));

    [Fact]
    public async Task ToggleFilter_KindChip_AddsTokenAndReplacesOtherKind()
    {
        // Arrange
        using var viewModel = await CreateAsync("отчёт");
        var documents = Chip(viewModel, "Документы");
        var images = Chip(viewModel, "Изображения");
        var replaced = 0;
        viewModel.QueryReplaced += (_, _) => replaced++;

        // Act
        viewModel.ToggleFilterCommand.Execute(documents);
        await viewModel.PendingRefresh;
        var withDocuments = viewModel.Query;
        viewModel.ToggleFilterCommand.Execute(images);
        await viewModel.PendingRefresh;

        // Assert
        withDocuments.ShouldBe("отчёт type:document");
        viewModel.Query.ShouldBe("отчёт type:image", "в группе вида активен один чип");
        images.IsActive.ShouldBeTrue();
        documents.IsActive.ShouldBeFalse();
        replaced.ShouldBe(2);
    }

    [Fact]
    public async Task ToggleFilter_ActiveChip_RemovesToken()
    {
        // Arrange
        using var viewModel = await CreateAsync("бюджет modified:week");
        var week = Chip(viewModel, "Неделя");

        // Act
        var activeBefore = week.IsActive;
        viewModel.ToggleFilterCommand.Execute(week);
        await viewModel.PendingRefresh;

        // Assert
        activeBefore.ShouldBeTrue("фильтр, набранный руками, подсвечивает чип");
        viewModel.Query.ShouldBe("бюджет");
        week.IsActive.ShouldBeFalse();
    }

    [Fact]
    public async Task RootChips_SeveralRoots_AppearWithQuotedLabels()
    {
        // Arrange & Act
        using var viewModel = await CreateAsync(string.Empty);

        // Assert
        var roots = viewModel.FilterChips.Where(static chip => chip.Group == FilterGroup.Root).ToList();
        roots.Count.ShouldBe(SampleData.Roots.Count);
        roots.ShouldContain(chip => chip.Token == "root:\"" + SampleData.Roots[1] + "\"" || chip.Token == "root:" + SampleData.Roots[1]);
    }

    [Fact]
    public async Task Suggestions_EmptyQuery_ShowHistoryThatCanBeDeleted()
    {
        // Arrange
        using var viewModel = await CreateAsync(string.Empty);

        // Act
        var title = viewModel.SuggestionsTitle;
        var history = viewModel.Suggestions.ToList();
        await viewModel.DeleteHistoryCommand.ExecuteAsync(history[0]);

        // Assert
        title.ShouldBe("История");
        viewModel.IsShowingHistory.ShouldBeTrue();
        history.Select(static item => item.Query).ShouldBe(["отчёт", "бюджет", "договор"]);
        viewModel.Suggestions.Select(static item => item.Query).ShouldBe(["бюджет", "договор"]);
    }

    [Fact]
    public async Task Suggestions_ClearHistory_HidesStrip()
    {
        // Arrange
        using var viewModel = await CreateAsync(string.Empty);

        // Act
        await viewModel.ClearHistoryCommand.ExecuteAsync(null);

        // Assert
        viewModel.Suggestions.ShouldBeEmpty();
        viewModel.ShowSuggestions.ShouldBeFalse();
        viewModel.NoticeTitle.ShouldBe("История очищена");
    }

    [Fact]
    public async Task Suggestions_TypingFilterValue_CompletesWithTab()
    {
        // Arrange
        using var viewModel = await CreateAsync("отчёт ext:do");

        // Act
        var title = viewModel.SuggestionsTitle;
        var first = viewModel.Suggestions[0];
        await viewModel.ExecuteAsync(QuickSearchCommand.AcceptSuggestion);
        await viewModel.PendingRefresh;

        // Assert
        title.ShouldBe("Подсказки");
        viewModel.IsShowingHistory.ShouldBeFalse();
        first.Display.ShouldBe("ext:docx");
        viewModel.Query.ShouldBe("отчёт ext:docx ", "после значения — пробел для следующего слова");
        viewModel.ShowSuggestions.ShouldBeFalse("после пробела дополнять нечего");
    }

    [Fact]
    public async Task Suggestions_TypingPlainWord_ShowNoHistoryNoise()
    {
        // Arrange & Act
        using var viewModel = await CreateAsync("бюд");

        // Assert
        viewModel.ShowSuggestions.ShouldBeFalse();
    }

    [Fact]
    public async Task Suggestions_FilterKeyPrefix_SuggestsKey()
    {
        // Arrange & Act
        using var viewModel = await CreateAsync("отчёт mod");

        // Assert
        viewModel.Suggestions.ShouldHaveSingleItem().Query.ShouldBe("отчёт modified:");
    }

    [Fact]
    public async Task HelpQuery_ShowsSyntaxInsteadOfSearching()
    {
        // Arrange & Act
        using var viewModel = await CreateAsync("?");

        // Assert
        viewModel.ShowHelp.ShouldBeTrue();
        viewModel.ShowResults.ShouldBeFalse();
        viewModel.Results.ShouldBeEmpty();
        viewModel.StatusText.ShouldStartWith("Синтаксис запроса");
        SearchHelp.Items.ShouldContain(item => item.Example.StartsWith("ext:pdf", StringComparison.Ordinal));
        SearchHelp.Items.Count(static item => item.IsHeader).ShouldBe(3);
    }

    [Fact]
    public async Task HelpQuery_ThenTyping_HidesHelp()
    {
        // Arrange
        using var viewModel = await CreateAsync("?");

        // Act
        viewModel.Query = "бюджет";
        await viewModel.PendingRefresh;

        // Assert
        viewModel.ShowHelp.ShouldBeFalse();
        viewModel.ShowResults.ShouldBeTrue();
    }

    [Fact]
    public async Task ToggleFilters_CtrlF_HidesAndShowsChips()
    {
        // Arrange
        using var viewModel = await CreateAsync(string.Empty);

        // Act
        await viewModel.ExecuteAsync(QuickSearchCommand.ToggleFilters);
        var hidden = !viewModel.ShowFilters;
        await viewModel.ExecuteAsync(QuickSearchCommand.ToggleFilters);

        // Assert
        hidden.ShouldBeTrue();
        viewModel.ShowFilters.ShouldBeTrue();
    }

    [Theory]
    [InlineData("отчёт \"годовой план\" ext:pdf", new[] { "отчёт", "\"годовой план\"", "ext:pdf" })]
    [InlineData("  a   b ", new[] { "a", "b" })]
    [InlineData("", new string[0])]
    public void QueryText_Tokenize_KeepsQuotedPhrases(string query, string[] expected)
    {
        // Act & Assert
        QueryText.Tokenize(query).ShouldBe(expected);
    }

    [Theory]
    [InlineData("отчёт type:pdf -type:image", "type", "type:video", "отчёт -type:image type:video")]
    [InlineData("отчёт TYPE:pdf", "type", null, "отчёт")]
    [InlineData("\"a:b\" in:docs", "in", null, "\"a:b\"")]
    public void QueryText_WithFilter_ReplacesGroupButKeepsNegations(string query, string key, string? token, string expected)
    {
        // Act & Assert
        QueryText.WithFilter(query, key, token).ShouldBe(expected);
    }

    [Fact]
    public void QueryText_KeyOfAndQuote()
    {
        // Act & Assert
        QueryText.KeyOf("-Ext:pdf").ShouldBe("ext");
        QueryText.KeyOf("слово").ShouldBeEmpty();
        QueryText.KeyOf("\"фраза:с двоеточием\"").ShouldBeEmpty();
        QueryText.Quote("Мои документы").ShouldBe("\"Мои документы\"");
        QueryText.Quote("Документы").ShouldBe("Документы");
    }

    [Fact]
    public void KeyMap_Tab_AcceptsSuggestionOnlyWhenShown()
    {
        // Arrange
        var withSuggestions = new QueryBoxState(IsEmpty: false, HasSelection: false, CaretAtEnd: true, HasSuggestions: true);
        var without = withSuggestions with { HasSuggestions = false };

        // Act & Assert
        QuickSearchKeyMap.Resolve(Avalonia.Input.Key.Tab, Avalonia.Input.KeyModifiers.None, withSuggestions).ShouldBe(new QuickSearchKey(QuickSearchCommand.AcceptSuggestion));
        QuickSearchKeyMap.Resolve(Avalonia.Input.Key.Tab, Avalonia.Input.KeyModifiers.None, without).ShouldBe(new QuickSearchKey(QuickSearchCommand.ContextMenu));
        QuickSearchKeyMap.Resolve(Avalonia.Input.Key.F, Avalonia.Input.KeyModifiers.Control, without).ShouldBe(new QuickSearchKey(QuickSearchCommand.ToggleFilters));
    }

    private static FilterChip Chip(QuickSearchViewModel viewModel, string label) => viewModel.FilterChips.Single(chip => chip.Label == label);

    private static async Task<QuickSearchViewModel> CreateAsync(string query)
    {
        var viewModel = new QuickSearchViewModel(new SampleSearchService(static () => Now), static () => Now) { DebounceDelay = TimeSpan.Zero };
        await viewModel.PendingRefresh;
        if (query.Length > 0)
        {
            viewModel.Query = query;
            await viewModel.PendingRefresh;
        }

        return viewModel;
    }
}

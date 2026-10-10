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
    public async Task Constructor_EmptyQuery_ShowsRecentFilesWithHeader()
    {
        // Arrange & Act
        using var viewModel = Create();
        await viewModel.PendingRefresh;

        // Assert
        viewModel.SectionHeader.ShouldBe("Недавно открытые");
        viewModel.Results.Count.ShouldBe(6);
        viewModel.Results[0].Entry.Name.ShouldBe("MainView.axaml");
        viewModel.SelectedIndex.ShouldBe(0);
        viewModel.ShowResults.ShouldBeTrue();
        viewModel.ShowEmptyState.ShouldBeFalse();
        viewModel.StatusText.ShouldContain("в индексе");
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
        viewModel.SectionHeader.ShouldBeEmpty();
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
        viewModel.SelectNumber(9).ShouldBeFalse("в недавних всего шесть строк");
        viewModel.SelectedIndex.ShouldBe(2);
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
        viewModel.SectionHeader.ShouldBe("Недавно открытые");
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

    private static QuickSearchViewModel Create()
        => new(new SampleSearchService(static () => Now), static () => Now) { DebounceDelay = TimeSpan.Zero };
}

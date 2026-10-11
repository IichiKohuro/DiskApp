using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Что показывают строки быстрого поиска (PLAN.md, этап 9.1): папка, размер и дата по настройкам; подборка при пустом
/// запросе отключается.
/// </summary>
public sealed class QuickSearchDisplayTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.FromHours(3));

    [Fact]
    public async Task Display_HidesColumnsInNewRows()
    {
        // Arrange
        using var viewModel = new QuickSearchViewModel(new SampleSearchService(static () => Now), static () => Now) { DebounceDelay = TimeSpan.Zero };
        viewModel.Query = "бюджет";
        await viewModel.PendingRefresh;

        // Act
        viewModel.Display = new SearchDisplay(ShowPath: false, ShowSize: true, ShowDate: false);
        await viewModel.PendingRefresh;

        // Assert
        viewModel.Results.ShouldNotBeEmpty();
        viewModel.Results.ShouldAllBe(hit => !hit.ShowPath && hit.ShowSize && !hit.ShowDate);
    }

    [Fact]
    public async Task Display_WithoutSuggestions_EmptyQueryShowsNoRows()
    {
        // Arrange
        using var viewModel = new QuickSearchViewModel(new SampleSearchService(static () => Now), static () => Now) { DebounceDelay = TimeSpan.Zero };
        await viewModel.PendingRefresh;
        var before = viewModel.Results.Count;

        // Act
        viewModel.Display = new SearchDisplay(ShowSuggestions: false);
        await viewModel.PendingRefresh;

        // Assert
        before.ShouldBeGreaterThan(0);
        viewModel.Results.ShouldBeEmpty();
    }

    [Fact]
    public async Task Display_SameValue_DoesNotRefresh()
    {
        // Arrange
        using var viewModel = new QuickSearchViewModel(new SampleSearchService(static () => Now), static () => Now) { DebounceDelay = TimeSpan.Zero };
        await viewModel.PendingRefresh;
        var pending = viewModel.PendingRefresh;

        // Act
        viewModel.Display = new SearchDisplay();

        // Assert
        viewModel.PendingRefresh.ShouldBeSameAs(pending);
    }
}

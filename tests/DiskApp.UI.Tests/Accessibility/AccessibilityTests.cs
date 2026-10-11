using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;
using DiskApp.UI.Views;

using FluentAvalonia.Styling;

namespace DiskApp.UI.Tests.Accessibility;

/// <summary>
/// Доступность на работающем интерфейсе (PLAN.md, этап 9.4): цели нажатия не меньше 32 px, строки списков называются
/// именем файла, высокая контрастность Windows важнее выбранной темы.
/// </summary>
public sealed class AccessibilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 15, 0, 0, TimeSpan.FromHours(3));

    [Theory]
    [InlineData(AppTheme.System, false, "Default")]
    [InlineData(AppTheme.Light, false, "Light")]
    [InlineData(AppTheme.Dark, false, "Dark")]
    [InlineData(AppTheme.Dark, true, "HighContrast")]
    [InlineData(AppTheme.Light, true, "HighContrast")]
    public void ThemeVariant_HighContrastWinsOverChoice(AppTheme theme, bool highContrast, string expected)
    {
        // Act
        var variant = AppThemeService.VariantFor(theme, highContrast);

        // Assert
        var name = variant == FluentAvaloniaTheme.HighContrastTheme ? "HighContrast"
            : variant == ThemeVariant.Light ? "Light"
            : variant == ThemeVariant.Dark ? "Dark"
            : "Default";
        name.ShouldBe(expected);
    }

    [Fact]
    public Task QuickSearch_FilterChipsAndRows_AreLargeAndNamed() => HeadlessTest.RunAsync(static async () =>
    {
        // Arrange
        using var viewModel = new QuickSearchViewModel(new SampleSearchService(static () => Now), new SampleFileActions(), static () => Now) { DebounceDelay = TimeSpan.Zero };
        var view = new QuickSearchView { DataContext = viewModel };
        var window = new Window { Width = 720, Height = 500, Content = view };
        window.Show();
        viewModel.Query = "бюджет";
        await viewModel.PendingRefresh;
        Dispatcher.UIThread.RunJobs();

        // Act
        var chips = view.GetVisualDescendants().OfType<Button>().Where(static button => button.Classes.Contains("filter")).ToList();
        var rows = view.FindControl<ListBox>("ResultsList")!.GetVisualDescendants().OfType<ListBoxItem>()
            .Where(static item => item.DataContext is DiskApp.UI.Models.SearchHit)
            .Select(static item => ControlAutomationPeer.CreatePeerForElement(item).GetName())
            .ToList();
        var queryName = ControlAutomationPeer.CreatePeerForElement(view.FindControl<TextBox>("QueryBox")!).GetName();

        // Assert
        chips.ShouldNotBeEmpty();
        chips.ShouldAllBe(static chip => chip.Bounds.Height >= 32);
        rows.ShouldContain("Бюджет 2026.xlsx", "строка называется именем файла, а не именем типа");
        queryName.ShouldBe("Найти файл или папку…");
        window.Close();
    });
}

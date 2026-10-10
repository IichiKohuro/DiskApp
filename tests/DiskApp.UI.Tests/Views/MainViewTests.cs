using System.Collections;

using Avalonia;
using Avalonia.Controls;

using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;
using DiskApp.UI.Views;

using FluentAvalonia.Styling;

namespace DiskApp.UI.Tests.Views;

/// <summary>
/// Представления на headless-платформе: тема загружается, страницы строятся без ошибок разметки и ресурсов,
/// навигация и список результатов отражают ViewModel.
/// </summary>
public sealed class MainViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 15, 0, 0, TimeSpan.FromHours(3));

    [Fact]
    public Task App_WhenInitialized_UsesFluentAvaloniaTheme() => HeadlessTest.RunAsync(static () =>
    {
        // Arrange
        var application = Application.Current;

        // Act
        var hasFluentAvaloniaTheme = application?.Styles.OfType<FluentAvaloniaTheme>().Any();

        // Assert
        application.ShouldBeOfType<App>();
        hasFluentAvaloniaTheme.ShouldBe(true);
    });

    [Fact]
    public Task MainView_WithViewModel_ShowsAllPagesInNavigation() => HeadlessTest.RunAsync(static () =>
    {
        // Arrange
        var viewModel = DesignData.Main;
        var view = new MainView { DataContext = viewModel };
        var window = new Window { Width = 1100, Height = 700, Content = view };

        // Act
        window.Show();
        var navigation = view.NavigationView;

        // Assert
        CountItems(navigation.MenuItemsSource).ShouldBe(7);
        CountItems(navigation.FooterMenuItemsSource).ShouldBe(1);
        navigation.SelectedItem.ShouldBeOfType<HomeViewModel>();
        window.Close();
    });

    [Fact]
    public Task MainView_NavigateToSettings_SwitchesSelectedItem() => HeadlessTest.RunAsync(static () =>
    {
        // Arrange
        var viewModel = DesignData.Main;
        var view = new MainView { DataContext = viewModel };
        var window = new Window { Width = 1100, Height = 700, Content = view };
        window.Show();

        // Act
        viewModel.NavigateTo(PageKeys.Settings);

        // Assert
        view.NavigationView.SelectedItem.ShouldBeOfType<SettingsViewModel>();
        window.Close();
    });

    [Fact]
    public Task QuickSearchView_AfterTyping_ListsResults() => HeadlessTest.RunAsync(static async () =>
    {
        // Arrange
        using var viewModel = new QuickSearchViewModel(new SampleSearchService(static () => Now), static () => Now) { DebounceDelay = TimeSpan.Zero };
        var view = new QuickSearchView { DataContext = viewModel };
        var window = new Window { Width = 720, Height = 500, Content = view };
        window.Show();
        var list = view.FindControl<ListBox>("ResultsList");

        // Act
        viewModel.Query = "pdf";
        await viewModel.PendingRefresh;

        // Assert
        list.ShouldNotBeNull();
        list.ItemCount.ShouldBe(viewModel.Results.Count);
        list.ItemCount.ShouldBeGreaterThan(0);
        list.SelectedIndex.ShouldBe(0);
        window.Close();
    });

    [Theory]
    [InlineData(PageKeys.Home)]
    [InlineData(PageKeys.Search)]
    [InlineData(PageKeys.Browse)]
    [InlineData(PageKeys.Settings)]
    public Task Page_WhenShownInWindow_LoadsWithoutErrors(string pageKey) => HeadlessTest.RunAsync(() =>
    {
        // Arrange
        var main = DesignData.Main;
        main.NavigateTo(pageKey).ShouldBeTrue();
        var presenter = new ContentControl { Content = main.SelectedPage };
        var window = new Window { Width = 1000, Height = 700, Content = presenter };

        // Act
        window.Show();

        // Assert
        presenter.Presenter.ShouldNotBeNull();
        presenter.Presenter.Child.ShouldNotBeNull($"для страницы «{pageKey}» не найден DataTemplate в App.axaml");
        window.Close();
    });

    private static int CountItems(object? source)
        => source is IEnumerable enumerable ? enumerable.Cast<object>().Count() : 0;
}

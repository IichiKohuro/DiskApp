using Avalonia;
using Avalonia.Controls;

using DiskApp.UI.Views;

using FluentAvalonia.Styling;

namespace DiskApp.UI.Tests.Views;

/// <summary>
/// Дымовые проверки каркаса интерфейса на headless-платформе: тема FluentAvalonia загружается, представление показывается.
/// </summary>
public sealed class MainViewTests
{
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
    public Task MainView_WhenShownInWindow_RendersPlaceholderText() => HeadlessTest.RunAsync(static () =>
    {
        // Arrange
        var view = new MainView();
        var window = new Window { Width = 400, Height = 300, Content = view };

        // Act
        window.Show();
        var placeholder = view.FindControl<TextBlock>("PlaceholderText");

        // Assert
        window.IsVisible.ShouldBeTrue();
        placeholder.ShouldNotBeNull();
        placeholder.Text.ShouldBe("DiskApp");
        placeholder.Bounds.Width.ShouldBeGreaterThan(0);
        placeholder.Bounds.Height.ShouldBeGreaterThan(0);
        window.Close();
    });
}

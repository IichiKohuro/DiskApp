using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Настройки: выбор темы применяется сразу, корни индекса загружаются, версия и действующий хоткей подписаны.
/// </summary>
public sealed class SettingsViewModelTests
{
    [Fact]
    public void Constructor_WithCurrentTheme_SelectsMatchingOption()
    {
        // Arrange
        var themes = Substitute.For<IThemeService>();
        themes.Current.Returns(AppTheme.Dark);

        // Act
        var viewModel = new SettingsViewModel(themes, new SampleSearchService(), new HotkeyStatus());

        // Assert
        viewModel.SelectedTheme.Theme.ShouldBe(AppTheme.Dark);
        viewModel.Themes.Select(option => option.Theme).ShouldBe([AppTheme.System, AppTheme.Light, AppTheme.Dark]);
        themes.DidNotReceive().Apply(Arg.Any<AppTheme>());
    }

    [Fact]
    public void SelectedTheme_Changed_AppliesThemeImmediately()
    {
        // Arrange
        var themes = Substitute.For<IThemeService>();
        var viewModel = new SettingsViewModel(themes, new SampleSearchService(), new HotkeyStatus());

        // Act
        viewModel.SelectedTheme = viewModel.Themes.Single(option => option.Theme == AppTheme.Light);

        // Assert
        themes.Received(1).Apply(AppTheme.Light);
    }

    [Fact]
    public async Task Initialization_WithSampleIndex_LoadsRootsAndVersion()
    {
        // Arrange
        var viewModel = new SettingsViewModel(Substitute.For<IThemeService>(), new SampleSearchService(), new HotkeyStatus());

        // Act
        await viewModel.Initialization;

        // Assert
        viewModel.Roots.Count.ShouldBe(4);
        viewModel.Roots[0].ShouldBe(@"D:\Projects");
        viewModel.VersionText.ShouldStartWith("Версия 0.");
    }

    [Fact]
    public void Hotkeys_WhenShellRegistersFallback_ReflectsActiveCombination()
    {
        // Arrange
        var hotkeys = new HotkeyStatus();
        var viewModel = new SettingsViewModel(Substitute.For<IThemeService>(), new SampleSearchService(), hotkeys);

        // Act
        hotkeys.QuickSearchText = "Ctrl + Alt + F";
        hotkeys.IsRegistered = true;
        hotkeys.Message = "Сочетание Ctrl + Alt + D занято.";

        // Assert
        viewModel.Hotkeys.QuickSearchText.ShouldBe("Ctrl + Alt + F");
        viewModel.Hotkeys.IsRegistered.ShouldBeTrue();
        HotkeyStatus.DefaultQuickSearchText.ShouldBe("Ctrl + Alt + D");
    }
}

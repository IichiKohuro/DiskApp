using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Переключатель Quick Switch в настройках (PLAN.md, этап 8.6): состояние из сервиса, включение и откат при ошибке.
/// </summary>
public sealed class SettingsQuickSwitchTests
{
    [Fact]
    public void Constructor_ReadsStateAndHotkey()
    {
        // Arrange
        var quickSwitch = Substitute.For<IQuickSwitchSettings>();
        quickSwitch.IsSupported.Returns(true);
        quickSwitch.IsEnabled.Returns(true);
        quickSwitch.HotkeyText.Returns("Ctrl + Alt + G");

        // Act
        var viewModel = Create(quickSwitch);

        // Assert
        viewModel.IsQuickSwitchSupported.ShouldBeTrue();
        viewModel.QuickSwitchEnabled.ShouldBeTrue();
        viewModel.QuickSwitchDescription.ShouldContain("Ctrl + Alt + G");
        quickSwitch.DidNotReceive().SetEnabled(Arg.Any<bool>());
    }

    [Fact]
    public void Toggle_On_EnablesFeature()
    {
        // Arrange
        var quickSwitch = Substitute.For<IQuickSwitchSettings>();
        quickSwitch.IsSupported.Returns(true);
        var viewModel = Create(quickSwitch);

        // Act
        viewModel.QuickSwitchEnabled = true;

        // Assert
        quickSwitch.Received(1).SetEnabled(true);
        viewModel.HasIntegrationNote.ShouldBeFalse();
    }

    [Fact]
    public void Toggle_HotkeyTaken_ShowsReasonAndTurnsBackOff()
    {
        // Arrange
        var quickSwitch = Substitute.For<IQuickSwitchSettings>();
        quickSwitch.IsSupported.Returns(true);
        quickSwitch.When(service => service.SetEnabled(true))
            .Do(_ => throw new InvalidOperationException("Quick Switch не включён: сочетание занято."));
        var viewModel = Create(quickSwitch);

        // Act
        viewModel.QuickSwitchEnabled = true;

        // Assert
        viewModel.QuickSwitchEnabled.ShouldBeFalse();
        viewModel.IntegrationNote.ShouldBe("Quick Switch не включён: сочетание занято.");
    }

    [Fact]
    public void Default_WithoutService_IsUnsupportedAndOff()
    {
        // Act
        var viewModel = new SettingsViewModel(Substitute.For<IThemeService>(), new SampleSearchService(), new HotkeyStatus());

        // Assert
        viewModel.IsQuickSwitchSupported.ShouldBeFalse();
        viewModel.QuickSwitchEnabled.ShouldBeFalse();
        viewModel.QuickSwitchDescription.ShouldContain(Hotkey.DefaultQuickSwitch.DisplayText);
    }

    private static SettingsViewModel Create(IQuickSwitchSettings quickSwitch)
        => new(Substitute.For<IThemeService>(), new SampleSearchService(), new HotkeyStatus(), quickSwitch: quickSwitch);
}

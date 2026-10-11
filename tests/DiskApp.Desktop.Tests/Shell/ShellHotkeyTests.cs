using DiskApp.Core.Settings;
using DiskApp.Desktop.Shell;
using DiskApp.UI.Services;

namespace DiskApp.Desktop.Tests.Shell;

/// <summary>
/// Сочетания оболочки (PLAN.md, этап 18.1, ADR-0006, ADR-0011): порядок кандидатов, запасные варианты не отнимают
/// сочетания других функций DiskApp, значения по умолчанию в ядре и интерфейсе совпадают.
/// </summary>
public sealed class ShellHotkeyTests
{
    [Fact]
    public void Candidates_ConfiguredFirstThenFallbacks()
    {
        // Act
        var candidates = ShellHotkey.Candidates("Ctrl+Alt+K", HotkeyTarget.MainWindow, reserved: []);

        // Assert
        candidates.Recognized.ShouldBeTrue();
        candidates.Preferred.ToString().ShouldBe("Ctrl+Alt+K");
        candidates.List.Select(static hotkey => hotkey.ToString()).ShouldBe(["Ctrl+Alt+K", "Ctrl+Alt+O", "Ctrl+Alt+W", "Ctrl+Alt+M"]);
    }

    [Fact]
    public void Candidates_UnrecognizedSetting_UsesDefault()
    {
        // Act
        var candidates = ShellHotkey.Candidates("Shift+Q", HotkeyTarget.DropWindow, reserved: []);

        // Assert
        candidates.Recognized.ShouldBeFalse();
        candidates.Preferred.ShouldBe(Hotkey.DefaultDropWindow);
        candidates.List[0].ShouldBe(Hotkey.DefaultDropWindow);
    }

    [Fact]
    public void Candidates_SkipFallbacksReservedByOtherFunctions()
    {
        // Arrange: пользователь отдал Ctrl+Alt+W окну Drag&Drop — главное окно не возьмёт его запасным
        Hotkey.TryParse("Ctrl+Alt+W", out var reserved).ShouldBeTrue();

        // Act
        var candidates = ShellHotkey.Candidates("Ctrl+Alt+O", HotkeyTarget.MainWindow, [reserved!]);

        // Assert
        candidates.List.ShouldNotContain(reserved!);
        candidates.List.Select(static hotkey => hotkey.ToString()).ShouldBe(["Ctrl+Alt+O", "Ctrl+Alt+M"]);
    }

    [Fact]
    public void Defaults_InCoreSettingsMatchInterface()
    {
        // Arrange
        var settings = new HotkeySettings();

        // Assert
        settings.QuickSearch.ShouldBe(Hotkey.DefaultFor(HotkeyTarget.QuickSearch).ToString());
        settings.QuickSwitch.ShouldBe(Hotkey.DefaultFor(HotkeyTarget.QuickSwitch).ToString());
        settings.MainWindow.ShouldBe(Hotkey.DefaultFor(HotkeyTarget.MainWindow).ToString());
        settings.DropWindow.ShouldBe(Hotkey.DefaultFor(HotkeyTarget.DropWindow).ToString());
    }
}

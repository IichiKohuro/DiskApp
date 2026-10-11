using Avalonia.Input;

using DiskApp.UI.Services;

namespace DiskApp.UI.Tests.Services;

/// <summary>
/// Модель глобального сочетания (PLAN.md, этап 6.5): разбор строки настроек, подпись, жест, запасные сочетания.
/// </summary>
public sealed class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+D", HotkeyModifiers.Control | HotkeyModifiers.Alt, "D")]
    [InlineData(" control + alt + space ", HotkeyModifiers.Control | HotkeyModifiers.Alt, "Space")]
    [InlineData("Win+Shift+F12", HotkeyModifiers.Windows | HotkeyModifiers.Shift, "F12")]
    [InlineData("Alt+1", HotkeyModifiers.Alt, "1")]
    [InlineData("ctrl+pageup", HotkeyModifiers.Control, "PageUp")]
    public void TryParse_ValidText_ReturnsHotkey(string text, HotkeyModifiers modifiers, string key)
    {
        // Act
        var parsed = Hotkey.TryParse(text, out var hotkey);

        // Assert
        parsed.ShouldBeTrue();
        hotkey!.Modifiers.ShouldBe(modifiers);
        hotkey.Key.ShouldBe(key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("D")]
    [InlineData("Shift+D")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+Ctrl+D")]
    [InlineData("Ctrl+D+E")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+Ж")]
    public void TryParse_InvalidText_ReturnsFalse(string? text)
        => Hotkey.TryParse(text, out _).ShouldBeFalse();

    [Fact]
    public void Text_DisplayAndSettingsForms()
    {
        // Arrange
        Hotkey.TryParse("alt+ctrl+shift+d", out var hotkey).ShouldBeTrue();

        // Act & Assert: порядок модификаторов канонический
        hotkey!.DisplayText.ShouldBe("Ctrl + Alt + Shift + D");
        hotkey.ToString().ShouldBe("Ctrl+Alt+Shift+D");
        Hotkey.TryParse(hotkey.ToString(), out var roundTrip).ShouldBeTrue();
        roundTrip.ShouldBe(hotkey);
    }

    [Fact]
    public void ToGesture_MapsModifiersAndDigits()
    {
        // Arrange
        Hotkey.TryParse("Ctrl+Win+5", out var hotkey).ShouldBeTrue();

        // Act
        var gesture = hotkey!.ToGesture();

        // Assert
        gesture.Key.ShouldBe(Key.D5);
        gesture.KeyModifiers.ShouldBe(KeyModifiers.Control | KeyModifiers.Meta);
    }

    [Fact]
    public void QuickSearchFallbacks_StartWithDefaultAndAreDistinct()
    {
        // Assert
        Hotkey.QuickSearchFallbacks[0].ShouldBe(Hotkey.DefaultQuickSearch);
        Hotkey.DefaultQuickSearch.ToString().ShouldBe("Ctrl+Alt+D");
        Hotkey.QuickSearchFallbacks.Distinct().Count().ShouldBe(Hotkey.QuickSearchFallbacks.Count);
    }
}

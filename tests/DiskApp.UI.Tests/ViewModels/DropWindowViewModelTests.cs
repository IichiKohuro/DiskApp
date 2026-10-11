using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Окно Drag&amp;Drop (PLAN.md, этап 18.1): разовая подсказка о новом поведении значка в трее и закрытие.
/// </summary>
public sealed class DropWindowViewModelTests
{
    private readonly ITipsService _tips = Substitute.For<ITipsService>();
    private readonly HotkeyStatus _hotkeys = new();

    [Fact]
    public void OnShown_FirstTime_ExplainsTrayClicksWithQuickSearchHotkey()
    {
        // Arrange
        _tips.ShouldShow(Tips.TrayClicks).Returns(true);
        _hotkeys.Set(HotkeyTarget.QuickSearch, "Ctrl + Alt + D", string.Empty);
        var viewModel = new DropWindowViewModel(_hotkeys, _tips);

        // Act
        viewModel.OnShown();

        // Assert
        viewModel.HasHint.ShouldBeTrue();
        viewModel.Hint.ShouldContain("двойным щелчком");
        viewModel.Hint.ShouldContain("Ctrl + Alt + D", Case.Sensitive, "сочетание не переносится по строкам");
        _tips.DidNotReceive().MarkShown(Arg.Any<string>());
    }

    [Fact]
    public void OnShown_QuickSearchHotkeyUnavailable_PointsToMenu()
    {
        // Arrange
        _tips.ShouldShow(Tips.TrayClicks).Returns(true);
        _hotkeys.Set(HotkeyTarget.QuickSearch, string.Empty, "занято");
        var viewModel = new DropWindowViewModel(_hotkeys, _tips);

        // Act
        viewModel.OnShown();

        // Assert
        viewModel.Hint.ShouldContain("в меню значка");
    }

    [Fact]
    public void OnHidden_AfterHint_MarksTipShownAndClearsIt()
    {
        // Arrange
        _tips.ShouldShow(Tips.TrayClicks).Returns(true);
        var viewModel = new DropWindowViewModel(_hotkeys, _tips);
        viewModel.OnShown();

        // Act
        viewModel.OnHidden();

        // Assert
        _tips.Received(1).MarkShown(Tips.TrayClicks);
        viewModel.HasHint.ShouldBeFalse();
    }

    [Fact]
    public void OnShown_TipAlreadyShown_ShowsNothing()
    {
        // Arrange
        _tips.ShouldShow(Tips.TrayClicks).Returns(false);
        var viewModel = new DropWindowViewModel(_hotkeys, _tips);

        // Act
        viewModel.OnShown();
        viewModel.OnHidden();

        // Assert
        viewModel.HasHint.ShouldBeFalse();
        _tips.DidNotReceive().MarkShown(Arg.Any<string>());
    }

    [Fact]
    public void Close_RaisesCloseRequested()
    {
        // Arrange
        var viewModel = new DropWindowViewModel(_hotkeys, _tips);
        var raised = 0;
        viewModel.CloseRequested += (_, _) => raised++;

        // Act
        viewModel.CloseCommand.Execute(null);

        // Assert
        raised.ShouldBe(1);
    }
}

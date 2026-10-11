using Avalonia;

using DiskApp.Desktop.Windows;

namespace DiskApp.Desktop.Tests.Windows;

/// <summary>
/// Положение окна Drag&amp;Drop (PLAN.md, этап 18.1): у панели задач монитора с курсором — напротив курсора при щелчке по
/// значку, в углу у области уведомлений при хоткее; окно не выходит за рабочую область.
/// </summary>
public sealed class DropWindowPlacementTests
{
    private const int Margin = 12;

    private static readonly PixelRect Screen = new(0, 0, 1920, 1080);
    private static readonly PixelSize Window = new(360, 240);

    [Fact]
    public void BottomTaskbar_ClickOnIcon_StandsAboveCursor()
    {
        // Arrange: панель задач внизу высотой 48, курсор на значке
        var area = new PixelRect(0, 0, 1920, 1032);

        // Act
        var position = DropWindowPlacement.Compute(Screen, area, new PixelPoint(1500, 1050), Window, Margin);

        // Assert
        position.ShouldBe(new PixelPoint(1500 - 180, 1032 - 240 - Margin));
    }

    [Fact]
    public void BottomTaskbar_IconAtEdge_StaysInsideWorkingArea()
    {
        // Arrange
        var area = new PixelRect(0, 0, 1920, 1032);

        // Act
        var position = DropWindowPlacement.Compute(Screen, area, new PixelPoint(1910, 1050), Window, Margin);

        // Assert
        position.X.ShouldBe(1920 - 360 - Margin);
    }

    [Fact]
    public void Hotkey_CursorAwayFromTaskbar_GoesToNotificationCorner()
    {
        // Arrange
        var area = new PixelRect(0, 0, 1920, 1032);

        // Act
        var position = DropWindowPlacement.Compute(Screen, area, new PixelPoint(400, 300), Window, Margin);

        // Assert
        position.ShouldBe(new PixelPoint(1920 - 360 - Margin, 1032 - 240 - Margin));
    }

    [Fact]
    public void TopTaskbar_StandsBelowIt()
    {
        // Arrange
        var area = new PixelRect(0, 48, 1920, 1032);

        // Act
        var position = DropWindowPlacement.Compute(Screen, area, new PixelPoint(1500, 20), Window, Margin);

        // Assert
        position.ShouldBe(new PixelPoint(1500 - 180, 48 + Margin));
    }

    [Fact]
    public void LeftTaskbar_StandsNextToItAgainstCursor()
    {
        // Arrange
        var area = new PixelRect(64, 0, 1856, 1080);

        // Act
        var position = DropWindowPlacement.Compute(Screen, area, new PixelPoint(30, 1000), Window, Margin);

        // Assert
        position.ShouldBe(new PixelPoint(64 + Margin, 1080 - 240 - Margin), "окно не выходит за нижний край");
    }

    [Fact]
    public void RightTaskbar_StandsNextToIt()
    {
        // Arrange
        var area = new PixelRect(0, 0, 1856, 1080);

        // Act
        var position = DropWindowPlacement.Compute(Screen, area, new PixelPoint(1890, 500), Window, Margin);

        // Assert
        position.ShouldBe(new PixelPoint(1856 - 360 - Margin, 500 - 120));
    }

    [Fact]
    public void HiddenTaskbar_UsesBottomRightCorner()
    {
        // Act
        var position = DropWindowPlacement.Compute(Screen, Screen, new PixelPoint(1900, 1079), Window, Margin);

        // Assert
        position.ShouldBe(new PixelPoint(1920 - 360 - Margin, 1080 - 240 - Margin));
    }

    [Fact]
    public void SecondMonitor_KeepsItsOffset()
    {
        // Arrange: монитор справа от основного, панель задач внизу
        var bounds = new PixelRect(1920, 0, 2560, 1440);
        var area = new PixelRect(1920, 0, 2560, 1392);

        // Act
        var position = DropWindowPlacement.Compute(bounds, area, new PixelPoint(4000, 1420), Window, Margin);

        // Assert
        position.ShouldBe(new PixelPoint(4000 - 180, 1392 - 240 - Margin));
    }
}

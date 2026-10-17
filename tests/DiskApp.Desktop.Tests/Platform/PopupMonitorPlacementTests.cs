using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Avalonia;

using DiskApp.Desktop.Platform.Windows;

namespace DiskApp.Desktop.Tests.Platform;

/// <summary>
/// Всплывающие окна Avalonia создаются на мониторе места показа, а не на основном (FEEDBACK.md, 2026-10-17:
/// подсказка «D:» вместо пути рабочей папки на мониторе 100 % рядом с основным 125 %).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PopupMonitorPlacementTests
{
    private const uint WsPopup = 0x80000000;
    private const uint WsExToolWindow = 0x00000080;

    [Theory]
    [InlineData(WsPopup, WsExToolWindow, 1, "Avalonia-9be5bfbc", true)]
    [InlineData(WsPopup, WsExToolWindow, 0, "Avalonia-9be5bfbc", false)]
    [InlineData(WsPopup, 0u, 1, "Avalonia-9be5bfbc", false)]
    [InlineData(0u, WsExToolWindow, 1, "Avalonia-9be5bfbc", false)]
    [InlineData(WsPopup, WsExToolWindow, 1, "#32768", false)]
    public void IsAvaloniaPopup_OnlyOwnedAvaloniaToolPopups(uint style, uint exStyle, int owner, string className, bool expected)
        => PopupMonitorPlacement.IsAvaloniaPopup(style, exStyle, owner, className).ShouldBe(expected);

    [Fact]
    public void ChooseAnchor_CursorOverOwner_IsCursor()
    {
        // Arrange
        var owner = new PixelRect(2100, 100, 1300, 850);

        // Act
        var anchor = PopupMonitorPlacement.ChooseAnchor(owner, new PixelPoint(3347, 410));

        // Assert
        anchor.ShouldBe(new PixelPoint(3347, 410));
    }

    [Fact]
    public void ChooseAnchor_CursorElsewhereOrUnknown_IsOwnerCenter()
    {
        // Arrange
        var owner = new PixelRect(2100, 100, 1300, 850);

        // Act
        var elsewhere = PopupMonitorPlacement.ChooseAnchor(owner, new PixelPoint(500, 500));
        var unknown = PopupMonitorPlacement.ChooseAnchor(owner, null);

        // Assert
        elsewhere.ShouldBe(new PixelPoint(2750, 525));
        unknown.ShouldBe(new PixelPoint(2750, 525));
    }

    [Fact]
    public void Install_OwnedToolPopupCreatedOffScreen_StartsOnOwnersMonitor()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Хук создания окон есть только в Windows.");
        }

        // Arrange: владелец на основном мониторе — его начало координат (0, 0) есть всегда.
        var monitor = NativeMethods.MonitorFromPoint(default, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfo { Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        NativeMethods.GetMonitorInfoW(monitor, ref info).ShouldBeTrue();
        var owner = NativeMethods.CreateWindowExW(0, "Static", "owner", WsPopup, info.Work.Left + 20, info.Work.Top + 20, 100, 100, 0, 0, 0, 0);
        owner.ShouldNotBe(0);
        nint popup = 0;
        nint unrelated = 0;

        try
        {
            // Act
            using (PopupMonitorPlacement.Install(classPrefix: "Static"))
            {
                popup = NativeMethods.CreateWindowExW(WsExToolWindow, "Static", "popup", WsPopup, -32000, -32000, 10, 10, owner, 0, 0, 0);
                unrelated = NativeMethods.CreateWindowExW(0, "Static", "unrelated", WsPopup, -32000, -32000, 10, 10, owner, 0, 0, 0);
            }

            // Assert
            NativeMethods.GetWindowRect(popup, out var placed).ShouldBeTrue();
            placed.Left.ShouldBe(info.Work.Left);
            placed.Top.ShouldBe(info.Work.Top);
            NativeMethods.GetWindowRect(unrelated, out var untouched).ShouldBeTrue();
            untouched.Left.ShouldBe(-32000, "без WS_EX_TOOLWINDOW окно не всплывающее");
        }
        finally
        {
            NativeMethods.DestroyWindow(unrelated);
            NativeMethods.DestroyWindow(popup);
            NativeMethods.DestroyWindow(owner);
        }
    }
}

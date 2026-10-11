using System.Runtime.Versioning;

using DiskApp.Desktop.Platform.Windows;

using Microsoft.Win32;

namespace DiskApp.Desktop.Tests.Platform;

/// <summary>
/// Автозапуск через реестр (PLAN.md, этап 6.6) на отдельном тестовом ключе — настоящий автозапуск не трогается;
/// коды клавиш для глобальных сочетаний.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PlatformTests : IDisposable
{
    private readonly string _keyPath = @"Software\DiskApp.Tests\Run-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void AutoStart_EnableDisable_WritesCommandForThisExecutable()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Реестр есть только в Windows.");
        }

        // Arrange
        var service = new RegistryAutoStartService(@"C:\Program Files\DiskApp\DiskApp.exe", portable: false, _keyPath);

        // Act
        var before = service.IsEnabled;
        service.SetEnabled(true);
        var command = ReadValue();
        var enabled = service.IsEnabled;
        service.SetEnabled(false);

        // Assert
        before.ShouldBeFalse();
        command.ShouldBe("\"C:\\Program Files\\DiskApp\\DiskApp.exe\" --minimized");
        enabled.ShouldBeTrue();
        service.IsEnabled.ShouldBeFalse();
        ReadValue().ShouldBeNull();
        service.Warning.ShouldBeEmpty();
    }

    [Fact]
    public void AutoStart_OtherCopy_IsNotEnabledAndNotRemoved()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Реестр есть только в Windows.");
        }

        // Arrange
        var other = new RegistryAutoStartService(@"E:\Portable\DiskApp.exe", portable: true, _keyPath);
        var current = new RegistryAutoStartService(@"C:\Program Files\DiskApp\DiskApp.exe", portable: false, _keyPath);
        other.SetEnabled(true);

        // Act
        var currentEnabled = current.IsEnabled;
        current.SetEnabled(false);

        // Assert
        currentEnabled.ShouldBeFalse("значение указывает на другую копию");
        ReadValue().ShouldBe("\"E:\\Portable\\DiskApp.exe\" --minimized --portable", "чужое значение не удаляется");
        other.Warning.ShouldContain(@"E:\Portable");
    }

    [Theory]
    [InlineData("D", 0x44u)]
    [InlineData("0", 0x30u)]
    [InlineData("F1", 0x70u)]
    [InlineData("F24", 0x87u)]
    [InlineData("Space", 0x20u)]
    [InlineData("PageDown", 0x22u)]
    public void HotkeyKeys_MapToVirtualKeys(string key, uint expected)
        => HotkeyKeys.ToVirtualKey(key).ShouldBe(expected);

    [Fact]
    public void HotkeyKeys_UnknownKey_IsNull()
        => HotkeyKeys.ToVirtualKey("Ж").ShouldBeNull();

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);
        }
    }

    private string? ReadValue()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        return key?.GetValue("DiskApp") as string;
    }
}

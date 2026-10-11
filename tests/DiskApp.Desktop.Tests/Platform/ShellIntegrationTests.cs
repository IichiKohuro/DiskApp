using System.Runtime.Versioning;

using DiskApp.Desktop.Platform.Windows;
using DiskApp.UI.Services;

using Microsoft.Win32;

namespace DiskApp.Desktop.Tests.Platform;

/// <summary>
/// Интеграции с Проводником (PLAN.md, этапы 8.5 и 8.8) на тестовом ключе <c>HKCU\Software\DiskAppTests</c> и во
/// временной папке «Отправить» — настоящие меню и ярлыки не трогаются.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ShellIntegrationTests : IDisposable
{
    private const string Executable = @"C:\Program Files\DiskApp\DiskApp.exe";

    private readonly string _classes = @"Software\DiskAppTests\Classes-" + Guid.NewGuid().ToString("N");
    private readonly string _sendTo = Path.Combine(Path.GetTempPath(), "DiskApp.SendToTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ContextMenu_EnableDisable_WritesAndRemovesVerbs()
    {
        SkipUnlessWindows();

        // Arrange
        var service = new ShellIntegrationService(Executable, portable: false, _classes, _sendTo);

        // Act
        var before = service.IsEnabled(ShellIntegrationFeature.ContextMenu);
        service.SetEnabled(ShellIntegrationFeature.ContextMenu, true);
        var add = Read(@"*\shell\DiskApp.Add\command");
        var open = Read(@"Directory\Background\shell\DiskApp.Open\command");
        var enabled = service.IsEnabled(ShellIntegrationFeature.ContextMenu);
        service.SetEnabled(ShellIntegrationFeature.ContextMenu, false);

        // Assert
        before.ShouldBeFalse();
        add.ShouldBe($"\"{Executable}\" --import \"%1\"");
        open.ShouldBe($"\"{Executable}\" --page browse --path \"%V\"");
        enabled.ShouldBeTrue();
        service.IsEnabled(ShellIntegrationFeature.ContextMenu).ShouldBeFalse();
        Read(@"*\shell\DiskApp.Add\command").ShouldBeNull();
        Read(@"Directory\shell\DiskApp.Index\command").ShouldBeNull();
    }

    [Fact]
    public void Protocol_PortableCopy_PassesPortableFlagAndUrl()
    {
        SkipUnlessWindows();

        // Arrange
        var service = new ShellIntegrationService(@"D:\Portable\DiskApp.exe", portable: true, _classes, _sendTo);

        // Act
        service.SetEnabled(ShellIntegrationFeature.Protocol, true);

        // Assert
        service.IsEnabled(ShellIntegrationFeature.Protocol).ShouldBeTrue();
        Read(@"diskapp\shell\open\command").ShouldBe("\"D:\\Portable\\DiskApp.exe\" --portable \"%1\"");
        using var protocol = Registry.CurrentUser.OpenSubKey(_classes + @"\diskapp");
        protocol!.GetValue("URL Protocol").ShouldBe(string.Empty);
    }

    [Fact]
    public void AnotherCopy_IsNotEnabledAndNotRemoved()
    {
        SkipUnlessWindows();

        // Arrange
        new ShellIntegrationService(@"D:\Other\DiskApp.exe", portable: false, _classes, _sendTo).SetEnabled(ShellIntegrationFeature.ContextMenu, true);
        var service = new ShellIntegrationService(Executable, portable: false, _classes, _sendTo);

        // Act
        var enabled = service.IsEnabled(ShellIntegrationFeature.ContextMenu);
        service.SetEnabled(ShellIntegrationFeature.ContextMenu, false);

        // Assert
        enabled.ShouldBeFalse("меню указывает на другую копию");
        Read(@"*\shell\DiskApp.Add\command").ShouldNotBeNull("чужие пункты не удаляются");
    }

    [Fact]
    public void SendTo_EnableDisable_CreatesShortcutToThisExecutable()
    {
        SkipUnlessWindows();

        // Arrange: ярлык должен указывать на существующий файл — берём исполняемый файл теста.
        var executable = Environment.ProcessPath!;
        var service = new ShellIntegrationService(executable, portable: false, _classes, _sendTo);

        // Act
        service.SetEnabled(ShellIntegrationFeature.SendTo, true);
        var created = File.Exists(service.SendToShortcut);
        var enabled = service.IsEnabled(ShellIntegrationFeature.SendTo);
        service.SetEnabled(ShellIntegrationFeature.SendTo, false);

        // Assert
        created.ShouldBeTrue();
        enabled.ShouldBeTrue();
        File.Exists(service.SendToShortcut).ShouldBeFalse();
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            Registry.CurrentUser.DeleteSubKeyTree(_classes, throwOnMissingSubKey: false);
        }

        try
        {
            Directory.Delete(_sendTo, recursive: true);
        }
        catch (IOException)
        {
            // Временная папка теста.
        }
    }

    private static void SkipUnlessWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Реестр и ярлыки есть только в Windows.");
        }
    }

    private string? Read(string commandKey)
    {
        using var key = Registry.CurrentUser.OpenSubKey(_classes + "\\" + commandKey);
        return key?.GetValue(null) as string;
    }
}

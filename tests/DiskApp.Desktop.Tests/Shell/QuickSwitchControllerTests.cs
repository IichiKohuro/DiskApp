using System.Runtime.Versioning;

using DiskApp.Core.Abstractions;
using DiskApp.Core.Settings;
using DiskApp.Desktop.Platform.Windows;
using DiskApp.Desktop.Shell;

using Serilog;

namespace DiskApp.Desktop.Tests.Shell;

/// <summary>
/// Флаг и горячая клавиша Quick Switch (PLAN.md, этап 8.6): выбор сохраняется, сочетание регистрируется только при
/// включённой функции, занятое сочетание не включает её. В тестах — редкое сочетание <c>Ctrl+Alt+Shift+F24</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class QuickSwitchControllerTests
{
    private const string RareHotkey = "Ctrl+Alt+Shift+F24";

    [Fact]
    public void SetEnabled_BeforeAttach_SavesChoiceAndRegistersLater()
    {
        SkipUnlessWindows();

        // Arrange
        var store = new MemorySettingsStore(RareHotkey);
        using var controller = new QuickSwitchController(store, new LoggerConfiguration().CreateLogger());
        using var window = new MessageWindow();

        // Act
        controller.SetEnabled(true);
        var registeredBeforeAttach = controller.IsRegistered;
        controller.Attach(window, static () => { });
        var registeredAfterAttach = controller.IsRegistered;
        controller.SetEnabled(false);

        // Assert
        registeredBeforeAttach.ShouldBeFalse();
        registeredAfterAttach.ShouldBeTrue();
        controller.IsRegistered.ShouldBeFalse();
        store.Current.Experimental.QuickSwitch.ShouldBeFalse();
        store.Saves.ShouldBe(2);
    }

    [Fact]
    public void Attach_Disabled_DoesNotTakeHotkey()
    {
        SkipUnlessWindows();

        // Arrange
        var store = new MemorySettingsStore(RareHotkey);
        using var controller = new QuickSwitchController(store, new LoggerConfiguration().CreateLogger());
        using var window = new MessageWindow();

        // Act
        controller.Attach(window, static () => { });

        // Assert
        controller.IsEnabled.ShouldBeFalse();
        controller.IsRegistered.ShouldBeFalse("выключенная функция не занимает сочетание");
        controller.HotkeyText.ShouldBe("Ctrl + Alt + Shift + F24");
    }

    [Fact]
    public void SetEnabled_HotkeyTaken_ThrowsAndKeepsFeatureOff()
    {
        SkipUnlessWindows();

        // Arrange: первое сочетание держит другая копия.
        var holderStore = new MemorySettingsStore(RareHotkey);
        using var holder = new QuickSwitchController(holderStore, new LoggerConfiguration().CreateLogger());
        using var holderWindow = new MessageWindow();
        holder.Attach(holderWindow, static () => { });
        holder.SetEnabled(true);

        var store = new MemorySettingsStore(RareHotkey);
        using var controller = new QuickSwitchController(store, new LoggerConfiguration().CreateLogger());
        using var window = new MessageWindow();
        controller.Attach(window, static () => { });

        // Act
        var exception = Should.Throw<InvalidOperationException>(() => controller.SetEnabled(true));

        // Assert
        exception.Message.ShouldStartWith("Quick Switch не включён: Ctrl + Alt + Shift + F24");
        controller.IsRegistered.ShouldBeFalse();
        store.Current.Experimental.QuickSwitch.ShouldBeFalse();
        holder.SetEnabled(false);
    }

    [Fact]
    public async Task Reconfigure_SettingsChangedElsewhere_FollowsFlagAndHotkey()
    {
        SkipUnlessWindows();

        // Arrange
        var store = new MemorySettingsStore(RareHotkey);
        using var controller = new QuickSwitchController(store, new LoggerConfiguration().CreateLogger());
        using var window = new MessageWindow();
        controller.Attach(window, static () => { });

        // Act: флаг включили через API или файл настроек — контроллер узнаёт об этом по Reconfigure.
        await store.SaveAsync(store.Current with { Experimental = new ExperimentalSettings { QuickSwitch = true } }, TestContext.Current.CancellationToken);
        controller.Reconfigure();
        var registered = controller.IsRegistered;
        await store.SaveAsync(store.Current with { Hotkeys = store.Current.Hotkeys with { QuickSwitch = "Ctrl+Alt+Shift+F23" } }, TestContext.Current.CancellationToken);
        controller.Reconfigure();
        var text = controller.HotkeyText;
        var stillRegistered = controller.IsRegistered;
        await store.SaveAsync(store.Current with { Experimental = new ExperimentalSettings { QuickSwitch = false } }, TestContext.Current.CancellationToken);
        controller.Reconfigure();

        // Assert
        registered.ShouldBeTrue();
        text.ShouldBe("Ctrl + Alt + Shift + F23");
        stillRegistered.ShouldBeTrue("новое сочетание зарегистрировано вместо старого");
        controller.IsRegistered.ShouldBeFalse();
    }

    private static void SkipUnlessWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Глобальные сочетания есть только в Windows.");
        }
    }

    private sealed class MemorySettingsStore(string hotkey) : ISettingsStore<AppSettings>
    {
        public AppSettings Current { get; private set; } = new() { Hotkeys = new HotkeySettings { QuickSwitch = hotkey } };

        public int Saves { get; private set; }

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Current = settings;
            Saves++;
            return Task.CompletedTask;
        }

        public IDisposable Subscribe(Action<AppSettings> onChanged) => new Unsubscriber();

        private sealed class Unsubscriber : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}

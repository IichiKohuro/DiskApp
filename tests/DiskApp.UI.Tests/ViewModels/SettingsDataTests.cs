using System.Text.Json;

using Avalonia.Input;

using DiskApp.Contracts;
using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Горячие клавиши и «Данные» в настройках (PLAN.md, этап 9.1): запись сочетания, проверка занятости, сброс; сведения
/// о данных, пересборка, очистка кэша, выгрузка и загрузка настроек.
/// </summary>
public sealed class SettingsDataTests : IDisposable
{
    private readonly SampleSettingsService _settings = new();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly IInfoBarService _notices = Substitute.For<IInfoBarService>();
    private readonly IAppMaintenance _maintenance = Substitute.For<IAppMaintenance>();
    private readonly IHotkeyChecker _checker = Substitute.For<IHotkeyChecker>();
    private readonly TemporaryDirectory _folder = new("settings-data");

    public SettingsDataTests()
    {
        _maintenance.IsSupported.Returns(true);
        _maintenance.DataDirectory.Returns(@"D:\DiskApp\data");
        _maintenance.LogsDirectory.Returns(@"D:\DiskApp\data\logs");
        _maintenance.DatabaseSize.Returns(5L * 1024 * 1024);
        _maintenance.CacheSize.Returns(2048L);
    }

    [Theory]
    [InlineData(Key.A, "A")]
    [InlineData(Key.D7, "7")]
    [InlineData(Key.F12, "F12")]
    [InlineData(Key.Space, "Space")]
    [InlineData(Key.PageDown, "PageDown")]
    [InlineData(Key.OemComma, null)]
    public void KeyName_MapsSupportedKeys(Key key, string? expected)
        => SettingsViewModel.KeyName(key).ShouldBe(expected);

    [Fact]
    public async Task RecordKey_ModifiersThenKey_SavesHotkey()
    {
        // Arrange
        var page = await CreateAsync();
        page.StartRecordingCommand.Execute(HotkeyTarget.QuickSearch);

        // Act
        page.RecordKey(Key.LeftCtrl, KeyModifiers.Control).ShouldBeTrue("модификатор ждёт клавишу");
        page.RecordKey(Key.K, KeyModifiers.Control | KeyModifiers.Alt);
        await page.PendingSave;

        // Assert
        page.RecordingTarget.ShouldBeNull();
        page.QuickSearchHotkey.ShouldBe("Ctrl+Alt+K");
        (await _settings.GetAsync(TestContext.Current.CancellationToken)).Hotkeys.QuickSearch.ShouldBe("Ctrl+Alt+K");
    }

    [Fact]
    public async Task RecordKey_WithoutCtrlAltWin_ExplainsAndKeepsRecording()
    {
        // Arrange
        var page = await CreateAsync();
        page.StartRecordingCommand.Execute(HotkeyTarget.QuickSwitch);

        // Act
        page.RecordKey(Key.G, KeyModifiers.Shift);

        // Assert
        page.RecordingTarget.ShouldBe(HotkeyTarget.QuickSwitch);
        page.HotkeyNote.ShouldStartWith("Нужен хотя бы один");
        _settings.PatchCount.ShouldBe(0);
    }

    [Fact]
    public async Task RecordKey_Taken_ShowsReason()
    {
        // Arrange
        var page = await CreateAsync();
        _checker.Check(Arg.Any<Hotkey>(), HotkeyTarget.QuickSwitch).Returns("сочетание занято другим приложением.");
        page.StartRecordingCommand.Execute(HotkeyTarget.QuickSwitch);

        // Act
        page.RecordKey(Key.Q, KeyModifiers.Control | KeyModifiers.Alt);

        // Assert
        page.HotkeyNote.ShouldBe("Ctrl + Alt + Q: сочетание занято другим приложением.");
        page.RecordingTarget.ShouldBe(HotkeyTarget.QuickSwitch);
        _settings.PatchCount.ShouldBe(0);
    }

    [Fact]
    public async Task RecordKey_Escape_Cancels()
    {
        // Arrange
        var page = await CreateAsync();
        page.StartRecordingCommand.Execute(HotkeyTarget.QuickSearch);

        // Act
        page.RecordKey(Key.Escape, KeyModifiers.None);

        // Assert
        page.RecordingTarget.ShouldBeNull();
        page.RecordKey(Key.A, KeyModifiers.Control).ShouldBeFalse("без записи нажатия идут странице");
    }

    [Fact]
    public async Task ResetHotkey_RestoresDefault()
    {
        // Arrange
        var page = await CreateAsync();
        page.ApplyHotkey(HotkeyTarget.QuickSwitch, new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, "Q"));
        await page.PendingSave;

        // Act
        page.ResetHotkeyCommand.Execute(HotkeyTarget.QuickSwitch);
        await page.PendingSave;

        // Assert
        page.QuickSwitchHotkeyText.ShouldBe("Ctrl + Alt + G");
        (await _settings.GetAsync(TestContext.Current.CancellationToken)).Hotkeys.QuickSwitch.ShouldBe("Ctrl+Alt+G");
    }

    [Fact]
    public async Task Data_ShowsFoldersAndSizes()
    {
        // Act
        var page = await CreateAsync();

        // Assert
        page.IsMaintenanceSupported.ShouldBeTrue();
        page.DataDirectoryText.ShouldBe(@"D:\DiskApp\data");
        page.IndexSizeText.ShouldContain("база");
        page.IndexSizeText.ShouldContain("5");
        page.CacheSizeText.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task ClearCacheAndRebuild_AskFirst()
    {
        // Arrange
        var page = await CreateAsync();
        _dialogs.ConfirmAsync("Очистить кэш", Arg.Any<string>(), "Очистить").Returns(true);

        // Act
        await page.ClearCacheCommand.ExecuteAsync(null);
        await page.RebuildIndexCommand.ExecuteAsync(null);

        // Assert
        await _maintenance.Received(1).ClearCacheAsync();
        await _maintenance.DidNotReceive().RebuildIndexAsync();
    }

    [Fact]
    public async Task ExportThenImport_RoundTripsSettings()
    {
        // Arrange
        var page = await CreateAsync();
        var file = _folder.Resolve("settings.json");
        _dialogs.PickSaveFileAsync(Arg.Any<string>(), "diskapp-settings.json", "json").Returns(file);
        _dialogs.PickOpenFileAsync(Arg.Any<string>(), "json").Returns(file);
        page.Fuzzy = false;
        await page.PendingSave;

        // Act
        await page.ExportSettingsCommand.ExecuteAsync(null);
        var exported = JsonSerializer.Deserialize(await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken), DiskAppJsonContext.Default.SettingsDto);
        page.Fuzzy = true;
        await page.PendingSave;
        await page.ImportSettingsCommand.ExecuteAsync(null);

        // Assert
        exported!.Search.Fuzzy.ShouldBeFalse();
        page.Fuzzy.ShouldBeFalse("загруженные настройки показаны на странице");
        (await _settings.GetAsync(TestContext.Current.CancellationToken)).Search.Fuzzy.ShouldBeFalse();
    }

    [Fact]
    public async Task Import_NotSettings_ShowsNote()
    {
        // Arrange
        var page = await CreateAsync();
        var file = _folder.CreateFile("bad.json", "[1, 2]");
        _dialogs.PickOpenFileAsync(Arg.Any<string>(), "json").Returns(file);

        // Act
        await page.ImportSettingsCommand.ExecuteAsync(null);

        // Assert
        page.SettingsNote.ShouldBe("В файле нет настроек DiskApp.");
    }

    public void Dispose() => _folder.Dispose();

    private async Task<SettingsViewModel> CreateAsync()
    {
        var page = new SettingsViewModel(
            Substitute.For<IThemeService>(),
            new SampleSearchService(),
            new HotkeyStatus(),
            settings: _settings,
            dialogs: _dialogs,
            notices: _notices,
            roots: new SampleRootsService(),
            maintenance: _maintenance,
            hotkeyChecker: _checker);
        await page.Initialization;
        return page;
    }
}

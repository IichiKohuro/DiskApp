using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Avalonia.Input;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DiskApp.Contracts;
using DiskApp.UI.Localization;
using DiskApp.UI.Services;
using DiskApp.UI.Text;

using FluentAvalonia.UI.Controls;

using AvaloniaKey = Avalonia.Input.Key;

namespace DiskApp.UI.ViewModels;

/// <summary>
/// Горячие клавиши, «Данные» и «О программе» (PLAN.md, этап 9.1): запись сочетания с проверкой занятости и сбросом,
/// папки и размеры данных, пересборка индекса, очистка кэша, выгрузка и загрузка настроек, сторонние компоненты.
/// </summary>
public sealed partial class SettingsViewModel
{
    private static readonly JsonSerializerOptions ExportOptions = new(DiskAppJsonContext.Default.Options) { WriteIndented = true };

    private readonly IAppMaintenance _maintenance;
    private readonly IHotkeyChecker? _hotkeyChecker;

    /// <summary>Какое сочетание сейчас записывается; <c>null</c> — никакое.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecordingQuickSearch), nameof(IsRecordingQuickSwitch))]
    public partial HotkeyTarget? RecordingTarget { get; set; }

    /// <summary>Записывается сочетание быстрого поиска.</summary>
    public bool IsRecordingQuickSearch => RecordingTarget == HotkeyTarget.QuickSearch;

    /// <summary>Записывается сочетание Quick Switch.</summary>
    public bool IsRecordingQuickSwitch => RecordingTarget == HotkeyTarget.QuickSwitch;

    /// <summary>Почему записанное сочетание не принято; пусто — всё хорошо.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHotkeyNote))]
    public partial string HotkeyNote { get; set; } = string.Empty;

    /// <summary>Есть замечание к сочетанию.</summary>
    public bool HasHotkeyNote => HotkeyNote.Length > 0;

    /// <summary>Сочетание Quick Switch для подписи.</summary>
    public string QuickSwitchHotkeyText => Hotkey.TryParse(QuickSwitchHotkey, out var hotkey) ? hotkey.DisplayText : QuickSwitchHotkey;

    /// <summary>Действия с данными доступны.</summary>
    public bool IsMaintenanceSupported => _maintenance.IsSupported;

    /// <summary>Папка данных.</summary>
    [ObservableProperty]
    public partial string DataDirectoryText { get; set; } = string.Empty;

    /// <summary>Индекс: записей и размер базы.</summary>
    [ObservableProperty]
    public partial string IndexSizeText { get; set; } = string.Empty;

    /// <summary>Размер кэша иконок и миниатюр.</summary>
    [ObservableProperty]
    public partial string CacheSizeText { get; set; } = string.Empty;

    /// <summary>Сторонние компоненты.</summary>
    public IReadOnlyList<ThirdPartyComponent> ThirdParty => _maintenance.ThirdParty;

    /// <summary>Описание раздела обновлений.</summary>
    public string UpdatesText { get; } = L.T("Settings.CheckingForAndInstallingUpdatesWill");

    /// <summary>Страница релизов.</summary>
    public Uri ReleasesUri { get; } = new("https://github.com/IichiKohuro/DiskApp/releases");

    /// <summary>Сообщить о проблеме.</summary>
    public Uri IssuesUri { get; } = new("https://github.com/IichiKohuro/DiskApp/issues");

    /// <summary>
    /// Имя клавиши для настроек по клавише Avalonia: буква, цифра, F1–F24, Space, навигация, Pause; иначе <c>null</c>.
    /// </summary>
    public static string? KeyName(AvaloniaKey key) => key switch
    {
        >= AvaloniaKey.A and <= AvaloniaKey.Z => key.ToString(),
        >= AvaloniaKey.D0 and <= AvaloniaKey.D9 => ((int)(key - AvaloniaKey.D0)).ToString(System.Globalization.CultureInfo.InvariantCulture),
        >= AvaloniaKey.F1 and <= AvaloniaKey.F24 => key.ToString(),
        AvaloniaKey.Space or AvaloniaKey.Insert or AvaloniaKey.Home or AvaloniaKey.End or AvaloniaKey.Left or AvaloniaKey.Up or AvaloniaKey.Right or AvaloniaKey.Down or AvaloniaKey.Pause => key.ToString(),
        AvaloniaKey.PageUp => "PageUp",
        AvaloniaKey.PageDown => "PageDown",
        _ => null,
    };

    /// <summary>
    /// Нажатие во время записи сочетания: модификаторы копятся, <c>Esc</c> отменяет, клавиша с Ctrl, Alt или Win — сохраняется.
    /// </summary>
    /// <returns><c>true</c>, если нажатие обработано записью.</returns>
    public bool RecordKey(AvaloniaKey key, KeyModifiers modifiers)
    {
        if (RecordingTarget is not { } target)
        {
            return false;
        }

        if (key == AvaloniaKey.Escape && modifiers == KeyModifiers.None)
        {
            CancelRecording();
            return true;
        }

        if (key is AvaloniaKey.LeftCtrl or AvaloniaKey.RightCtrl or AvaloniaKey.LeftAlt or AvaloniaKey.RightAlt or AvaloniaKey.LeftShift or AvaloniaKey.RightShift or AvaloniaKey.LWin or AvaloniaKey.RWin)
        {
            return true;
        }

        if (KeyName(key) is not { } name)
        {
            HotkeyNote = L.T("Settings.ThisKeyWillNotWorkUse");
            return true;
        }

        var parts = new List<string>();
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(KeyModifiers.Meta))
        {
            parts.Add("Win");
        }

        parts.Add(name);
        if (!Hotkey.TryParse(string.Join('+', parts), out var hotkey))
        {
            HotkeyNote = L.T("Settings.AtLeastOneOfCtrlAlt");
            return true;
        }

        ApplyHotkey(target, hotkey);
        return true;
    }

    /// <summary>Сохраняет сочетание после проверки.</summary>
    public void ApplyHotkey(HotkeyTarget target, Hotkey hotkey)
    {
        ArgumentNullException.ThrowIfNull(hotkey);

        if (_hotkeyChecker?.Check(hotkey, target) is { Length: > 0 } problem)
        {
            HotkeyNote = $"{hotkey.DisplayText}: {problem}";
            return;
        }

        RecordingTarget = null;
        HotkeyNote = string.Empty;
        if (target == HotkeyTarget.QuickSearch)
        {
            QuickSearchHotkey = hotkey.ToString();
            Save("hotkeys", "quickSearch", hotkey.ToString());
        }
        else
        {
            QuickSwitchHotkey = hotkey.ToString();
            Save("hotkeys", "quickSwitch", hotkey.ToString());
        }
    }

    partial void OnQuickSwitchHotkeyChanged(string value) => OnPropertyChanged(nameof(QuickSwitchHotkeyText));

    /// <summary>Начать запись сочетания.</summary>
    [RelayCommand]
    private void StartRecording(HotkeyTarget target)
    {
        HotkeyNote = string.Empty;
        RecordingTarget = target;
    }

    /// <summary>Отменить запись сочетания.</summary>
    [RelayCommand]
    private void CancelRecording()
    {
        RecordingTarget = null;
        HotkeyNote = string.Empty;
    }

    /// <summary>Вернуть сочетание по умолчанию.</summary>
    [RelayCommand]
    private void ResetHotkey(HotkeyTarget target)
        => ApplyHotkey(target, target == HotkeyTarget.QuickSearch ? Hotkey.DefaultQuickSearch : Hotkey.DefaultQuickSwitch);

    /// <summary>Перечитывает сведения о данных.</summary>
    private async Task RefreshDataAsync()
    {
        if (!_maintenance.IsSupported)
        {
            return;
        }

        DataDirectoryText = _maintenance.DataDirectory;
        CacheSizeText = Formatting.Size(_maintenance.CacheSize);
        try
        {
            var status = await _search.GetIndexStatusAsync(CancellationToken.None);
            IndexSizeText = L.F("SettingsData.IndexSize", Formatting.Count(status.TotalEntries, L.T("Common.Entry.One"), L.T("Common.Entry.Few"), L.T("Common.Entry.Many")), Formatting.Size(_maintenance.DatabaseSize));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            IndexSizeText = L.T("Settings.Database") + Formatting.Size(_maintenance.DatabaseSize);
        }
    }

    /// <summary>Открыть папку данных.</summary>
    [RelayCommand]
    private void OpenDataFolder() => _maintenance.OpenFolder(_maintenance.DataDirectory);

    /// <summary>Открыть папку журнала.</summary>
    [RelayCommand]
    private void OpenLogsFolder() => _maintenance.OpenFolder(_maintenance.LogsDirectory);

    /// <summary>Очистить кэш иконок и миниатюр.</summary>
    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        if (_dialogs is not null && !await _dialogs.ConfirmAsync(L.T("Settings.ClearCache"), L.T("Settings.DeleteSavedIconsAndThumbnailsThey"), L.T("Settings.Clear")))
        {
            return;
        }

        try
        {
            await _maintenance.ClearCacheAsync();
            await RefreshDataAsync();
            _notices?.Show(L.T("Settings.CacheCleared"), L.T("Settings.IconsAndThumbnailsWillLoadAgain"), FAInfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SettingsNote = L.T("Settings.CacheNotCleared") + exception.Message;
        }
    }

    /// <summary>Пересобрать индекс.</summary>
    [RelayCommand]
    private async Task RebuildIndexAsync()
    {
        if (_dialogs is not null
            && !await _dialogs.ConfirmAsync(L.T("Settings.RebuildIndex"), L.T("Settings.TheIndexOfAllRootsWill"), L.T("Settings.Rebuild")))
        {
            return;
        }

        try
        {
            await _maintenance.RebuildIndexAsync();
            _notices?.Show(L.T("Settings.RebuildingTheIndex"), L.T("Settings.ProgressIsShownInTheMain"), FAInfoBarSeverity.Informational);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SettingsNote = L.T("Settings.IndexNotRebuilt") + exception.Message;
        }
    }

    /// <summary>Сохранить настройки в файл JSON.</summary>
    [RelayCommand]
    private async Task ExportSettingsAsync()
    {
        if (_dialogs is null || await _dialogs.PickSaveFileAsync(L.T("Settings.SaveSettings"), "diskapp-settings.json", "json") is not { } path)
        {
            return;
        }

        try
        {
            var settings = await _settings.GetAsync();
            var json = JsonSerializer.Serialize(settings, ExportOptions.GetTypeInfo(typeof(Contracts.App.SettingsDto)));
            await File.WriteAllTextAsync(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _notices?.Show(L.T("Settings.SettingsSaved"), path, FAInfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SettingsNote = L.T("Settings.SettingsWereNotSavedToThe") + exception.Message;
        }
    }

    /// <summary>Загрузить настройки из файла JSON: значения проверяются, как при изменении на странице.</summary>
    [RelayCommand]
    private async Task ImportSettingsAsync()
    {
        if (_dialogs is null || await _dialogs.PickOpenFileAsync(L.T("Settings.LoadSettings"), "json") is not { } path)
        {
            return;
        }

        try
        {
            if (JsonNode.Parse(await File.ReadAllTextAsync(path)) is not JsonObject patch)
            {
                SettingsNote = L.T("Settings.TheFileHasNoDiskAppSettings");
                return;
            }

            await PendingSave;
            Apply(await _settings.PatchAsync(patch));
            SettingsNote = string.Empty;
            _notices?.Show(L.T("Settings.SettingsLoaded"), path, FAInfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            SettingsNote = L.T("Settings.SettingsWereNotLoaded") + exception.Message;
        }
    }
}

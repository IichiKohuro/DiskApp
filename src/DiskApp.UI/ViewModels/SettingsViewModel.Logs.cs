using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DiskApp.UI.Localization;
using DiskApp.UI.Text;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.ViewModels;

/// <summary>
/// Раздел «Журнал», диагностический архив и сброс настроек (PLAN.md, этап 9.5).
/// </summary>
public sealed partial class SettingsViewModel
{
    /// <summary>Ключ раздела «Журнал».</summary>
    public const string LogsCategory = "logs";

    private const int LogTailLines = 1000;

    private IReadOnlyList<LogLine> _logEntries = [];

    /// <summary>Записи журнала после фильтра.</summary>
    public ObservableCollection<LogLine> LogLines { get; } = [];

    /// <summary>Фильтры по уровню.</summary>
    public IReadOnlyList<SettingOption<LogSeverity>> LogFilters { get; } =
    [
        new(LogSeverity.Verbose, "Logs.All"),
        new(LogSeverity.Information, "Logs.InfoAndAbove"),
        new(LogSeverity.Warning, "Logs.WarningsAndAbove"),
        new(LogSeverity.Error, "Logs.ErrorsOnly"),
    ];

    /// <summary>Выбранный фильтр.</summary>
    [ObservableProperty]
    public partial SettingOption<LogSeverity>? SelectedLogFilter { get; set; }

    /// <summary>«Показано строк: 120 из 1 000 последних» или почему журнала нет.</summary>
    [ObservableProperty]
    public partial string LogsText { get; set; } = string.Empty;

    /// <summary>Перечитать хвост журнала.</summary>
    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task RefreshLogsAsync()
    {
        try
        {
            _logEntries = LogTail.Parse(await _maintenance.ReadLogTailAsync(LogTailLines));
            ApplyLogFilter();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logEntries = [];
            LogLines.Clear();
            LogsText = L.T("Logs.NotRead") + exception.Message;
        }
    }

    partial void OnSelectedLogFilterChanged(SettingOption<LogSeverity>? value) => ApplyLogFilter();

    partial void OnSelectedCategoryChanged(SettingsCategory value)
    {
        if (value.Key == LogsCategory)
        {
            _ = RefreshLogsCommand.ExecuteAsync(null);
        }
    }

    private void ApplyLogFilter()
    {
        // Новые записи сверху: обычно ищут то, что случилось только что.
        var minimum = SelectedLogFilter?.Value ?? LogSeverity.Verbose;
        LogLines.Clear();
        foreach (var entry in LogTail.AtLeast(_logEntries, minimum).Reverse())
        {
            LogLines.Add(entry);
        }

        LogsText = _logEntries.Count == 0 ? L.T("Logs.Empty") : L.F("Logs.Shown", LogLines.Count, _logEntries.Count);
    }

    /// <summary>Собрать диагностический архив и показать его в Проводнике.</summary>
    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task CreateDiagnosticArchiveAsync()
    {
        try
        {
            var path = await _maintenance.CreateDiagnosticArchiveAsync();
            SettingsNote = string.Empty;
            _notices?.Show(L.T("Settings.ArchiveReady"), path, FAInfoBarSeverity.Success);
            if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
            {
                _maintenance.OpenFolder(folder);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SettingsNote = L.T("Settings.ArchiveNotCollected") + exception.Message;
        }
    }

    /// <summary>Сбросить настройки к значениям по умолчанию после подтверждения.</summary>
    [RelayCommand]
    private async Task ResetSettingsAsync()
    {
        if (_dialogs is not null && !await _dialogs.ConfirmAsync(L.T("SafeMode.ResetTitle"), L.T("SafeMode.ResetMessage"), L.T("SafeMode.Reset")))
        {
            return;
        }

        try
        {
            var backup = await _maintenance.ResetSettingsAsync();
            SettingsNote = string.Empty;
            _notices?.Show(L.T("SafeMode.ResetDone"), backup is null ? string.Empty : L.F("SafeMode.BackupAt", backup), FAInfoBarSeverity.Success);
            await LoadSettingsAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SettingsNote = L.T("Settings.SettingsNotReset") + exception.Message;
        }
    }
}

using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DiskApp.UI.Localization;
using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.Text;

using FluentIcons.Common;

namespace DiskApp.UI.ViewModels;

/// <summary>
/// Карточка корня на главной (PLAN.md, этап 8.2): состояние, сводка, последнее сканирование, пересканирование и пауза.
/// </summary>
public sealed partial class RootCardViewModel : ObservableObject
{
    private readonly IRootsService _roots;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string, Exception> _onError;

    /// <summary>
    /// Создаёт карточку.
    /// </summary>
    /// <param name="info">Корень.</param>
    /// <param name="roots">Сервис корней.</param>
    /// <param name="clock">Часы для «5 мин назад».</param>
    /// <param name="onError">Показать ошибку действия.</param>
    public RootCardViewModel(RootInfo info, IRootsService roots, Func<DateTimeOffset> clock, Action<string, Exception> onError)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(onError);

        _roots = roots;
        _clock = clock;
        _onError = onError;
        Info = info;
    }

    /// <summary>Корень.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(LastScanText), nameof(IsPaused), nameof(CanPause), nameof(PauseText), nameof(Symbol), nameof(IsProblem), nameof(IsScanning))]
    public partial RootInfo Info { get; set; }

    /// <summary>Сводка: «12 345 файлов · 230 папок · 4,5 ГБ»; пусто, пока не загружена.</summary>
    [ObservableProperty]
    public partial string SummaryText { get; set; } = string.Empty;

    /// <summary>Состояние словами.</summary>
    public string StateText => Info.State switch
    {
        RootState.Offline => L.T("RootCard.Unavailable") + (Info.Message is { Length: > 0 } message ? ": " + message : string.Empty),
        RootState.Scanning => Info.Scanned is { } scanned ? L.F("RootCard.ScanningScanned", Formatting.Number(scanned)) : L.T("RootCard.Scanning"),
        RootState.Paused => L.T("RootCard.Paused"),
        RootState.Error => L.T("Common.Error") + (Info.Message is { Length: > 0 } error ? ": " + error : string.Empty),
        RootState.Disabled => L.T("RootCard.Disabled"),
        _ => L.T("RootCard.UpToDate"),
    };

    /// <summary>Когда сканировался.</summary>
    public string LastScanText => Info.LastScan is { } scan
        ? L.T("RootCard.Scanned") + Formatting.RelativeTime(scan.ToLocalTime(), _clock()).ToLower(CultureInfo.CurrentCulture)
        : L.T("RootCard.NotScannedYet");

    /// <summary>Индексация корня на паузе.</summary>
    public bool IsPaused => Info.State == RootState.Paused;

    /// <summary>Идёт сканирование.</summary>
    public bool IsScanning => Info.State == RootState.Scanning;

    /// <summary>Недоступен или ошибка — карточка выделяется.</summary>
    public bool IsProblem => Info.State is RootState.Offline or RootState.Error;

    /// <summary>Паузу можно поставить или снять: корень подключён к индексатору.</summary>
    public bool CanPause => Info.State is RootState.Online or RootState.Scanning or RootState.Paused;

    /// <summary>Подпись кнопки паузы.</summary>
    public string PauseText => IsPaused ? L.T("RootCard.Resume") : L.T("RootCard.Pause");

    /// <summary>Значок корня: диск, папка или отключённый диск.</summary>
    public Symbol Symbol => Info.State == RootState.Offline ? Symbol.PlugDisconnected
        : Info.Path.TrimEnd('\\', '/').Length <= 2 ? Symbol.HardDrive
        : Symbol.Folder;

    /// <summary>
    /// Загружает сводку корня.
    /// </summary>
    public async Task LoadSummaryAsync()
    {
        try
        {
            var summary = await _roots.GetSummaryAsync(Info.Id);
            SummaryText = $"{Formatting.Count(summary.FileCount, L.T("Common.File.One"), L.T("Common.File.Few"), L.T("Common.File.Many"))} · {Formatting.Count(summary.DirectoryCount, L.T("Common.Folder.One"), L.T("Common.Folder.Few"), L.T("Common.Folder.Many"))} · {Formatting.Size(summary.TotalSize)}";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SummaryText = string.Empty;
        }
    }

    /// <summary>Ставит полное сканирование в очередь.</summary>
    [RelayCommand]
    private async Task RescanAsync()
    {
        try
        {
            await _roots.RescanAsync(Info.Id);
            Info = Info with { State = RootState.Scanning, Scanned = null };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _onError(L.F("RootCard.CouldNotRescan", Info.Label), exception);
        }
    }

    /// <summary>Ставит индексацию корня на паузу или снимает с неё.</summary>
    [RelayCommand]
    private async Task TogglePauseAsync()
    {
        try
        {
            if (IsPaused)
            {
                await _roots.ResumeAsync(Info.Id);
                Info = Info with { State = RootState.Online };
            }
            else
            {
                await _roots.PauseAsync(Info.Id);
                Info = Info with { State = RootState.Paused };
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _onError(L.F("RootCard.CouldNotChangeThePauseOf", Info.Label), exception);
        }
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DiskApp.UI.Localization;
using DiskApp.UI.Services;

namespace DiskApp.UI.ViewModels;

/// <summary>
/// Окно «Что-то пошло не так» (PLAN.md, этап 9.5): необработанная ошибка уже записана в журнал, приложение продолжает
/// работу; отсюда собирается диагностический архив. После аварийного завершения то же окно открывается при следующем
/// запуске с причиной из прошлого сеанса.
/// </summary>
public sealed partial class ErrorReportViewModel : ViewModelBase
{
    private const int MaxDetailsLength = 20_000;

    private readonly IAppMaintenance _maintenance;

    /// <summary>Создаёт окно для ошибки.</summary>
    /// <param name="maintenance">Архив и папка журнала.</param>
    /// <param name="details">Текст ошибки: тип, сообщение, стек.</param>
    /// <param name="previousCrash">Ошибка из прошлого сеанса, завершившегося аварийно.</param>
    public ErrorReportViewModel(IAppMaintenance maintenance, string details, bool previousCrash = false)
    {
        ArgumentNullException.ThrowIfNull(maintenance);
        ArgumentNullException.ThrowIfNull(details);

        _maintenance = maintenance;
        IsPreviousCrash = previousCrash;
        Details = Trim(details);
    }

    /// <summary>Пользователь закрыл окно.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Ошибка из прошлого сеанса.</summary>
    public bool IsPreviousCrash { get; }

    /// <summary>Заголовок.</summary>
    public string Title => IsPreviousCrash ? L.T("Error.PreviousCrashTitle") : L.T("Error.Title");

    /// <summary>Пояснение.</summary>
    public string Message => IsPreviousCrash ? L.T("Error.PreviousCrashMessage") : L.T("Error.Message");

    /// <summary>Текст ошибки.</summary>
    [ObservableProperty]
    public partial string Details { get; set; }

    /// <summary>Сколько ошибок случилось, пока окно открыто.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoreText), nameof(HasMore))]
    public partial int MoreCount { get; set; }

    /// <summary>«Ещё ошибок с тех пор: 2».</summary>
    public string MoreText => L.F("Error.More", MoreCount);

    /// <summary>Были ещё ошибки.</summary>
    public bool HasMore => MoreCount > 0;

    /// <summary>Ход и итог сборки архива.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasArchiveText))]
    public partial string ArchiveText { get; set; } = string.Empty;

    /// <summary>Есть текст об архиве.</summary>
    public bool HasArchiveText => ArchiveText.Length > 0;

    /// <summary>Путь к собранному архиву.</summary>
    [ObservableProperty]
    public partial string? ArchivePath { get; set; }

    /// <summary>Пока ошибка показана, случилась ещё одна: окно не плодится, растёт счётчик.</summary>
    public void AddError(string details)
    {
        ArgumentNullException.ThrowIfNull(details);
        MoreCount++;
    }

    /// <summary>Собрать диагностический архив и показать его в Проводнике.</summary>
    [RelayCommand(AllowConcurrentExecutions = false)]
    private async Task CreateArchiveAsync()
    {
        ArchiveText = L.T("Error.Collecting");
        try
        {
            var path = await _maintenance.CreateDiagnosticArchiveAsync();
            ArchivePath = path;
            ArchiveText = L.F("Error.ArchiveReady", path);
            if (Path.GetDirectoryName(path) is { Length: > 0 } folder)
            {
                _maintenance.OpenFolder(folder);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ArchiveText = L.F("Error.ArchiveFailed", exception.Message);
        }
    }

    /// <summary>Открыть папку журнала.</summary>
    [RelayCommand]
    private void OpenLogs() => _maintenance.OpenFolder(_maintenance.LogsDirectory);

    /// <summary>Закрыть окно.</summary>
    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private static string Trim(string details) => details.Length > MaxDetailsLength ? details[..MaxDetailsLength] + "…" : details;
}

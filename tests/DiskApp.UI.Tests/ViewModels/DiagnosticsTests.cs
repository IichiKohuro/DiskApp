using DiskApp.UI.Services;
using DiskApp.UI.Text;
using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Диагностика (PLAN.md, этап 9.5): разбор журнала, раздел «Журнал» с фильтром по уровню, диагностический архив,
/// сброс настроек и окно «Что-то пошло не так».
/// </summary>
public sealed class DiagnosticsTests
{
    private static readonly string[] Log =
    [
        "    at Something.Before()",
        "10:00:00.000 [DBG] App: подробность",
        "10:00:01.000 [INF] App: сведения",
        "10:00:02.000 [WRN] App: предупреждение",
        "10:00:03.000 [ERR] App: ошибка",
        "System.InvalidOperationException: сломалось",
        "   at Program.Main()",
        "10:00:04.000 [FTL] App: сбой",
    ];

    private readonly IAppMaintenance _maintenance = Substitute.For<IAppMaintenance>();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly IInfoBarService _notices = Substitute.For<IInfoBarService>();

    public DiagnosticsTests()
    {
        _maintenance.IsSupported.Returns(true);
        _maintenance.LogsDirectory.Returns(@"C:\Data\logs");
        _maintenance.ThirdParty.Returns([]);
        _maintenance.ReadLogTailAsync(Arg.Any<int>()).Returns(Task.FromResult<IReadOnlyList<string>>(Log));
    }

    [Fact]
    public void Parse_KeepsStackWithItsEntry()
    {
        // Act
        var entries = LogTail.Parse(Log);

        // Assert
        entries.Select(static entry => entry.Severity).ShouldBe(
            [LogSeverity.Information, LogSeverity.Debug, LogSeverity.Information, LogSeverity.Warning, LogSeverity.Error, LogSeverity.Fatal]);
        entries[4].Text.ShouldBe("10:00:03.000 [ERR] App: ошибка\nSystem.InvalidOperationException: сломалось\n   at Program.Main()");
        entries[4].IsError.ShouldBeTrue();
        entries[3].IsWarning.ShouldBeTrue();
        LogTail.AtLeast(entries, LogSeverity.Warning).Count().ShouldBe(3);
    }

    [Fact]
    public async Task LogsCategory_LoadsTailAndFiltersByLevel()
    {
        // Arrange
        var page = Create();
        await page.Initialization;

        // Act
        page.SelectedCategory = page.Categories.Single(static category => category.Key == SettingsViewModel.LogsCategory);
        await page.RefreshLogsCommand.ExecutionTask!;
        var defaultCount = page.LogLines.Count;
        page.SelectedLogFilter = page.LogFilters.Single(static filter => filter.Value == LogSeverity.Error);

        // Assert
        defaultCount.ShouldBe(5, "по умолчанию — сведения и важнее");
        page.LogLines.Select(static line => line.Severity).ShouldBe([LogSeverity.Fatal, LogSeverity.Error], "новые сверху");
        page.LogsText.ShouldBe("Показано записей: 2 из 6 последних");
    }

    [Fact]
    public async Task LogsCategory_ReadFails_ShowsReason()
    {
        // Arrange
        _maintenance.ReadLogTailAsync(Arg.Any<int>()).Returns(Task.FromException<IReadOnlyList<string>>(new IOException("файл занят")));
        var page = Create();
        await page.Initialization;

        // Act
        await page.RefreshLogsCommand.ExecuteAsync(null);

        // Assert
        page.LogLines.ShouldBeEmpty();
        page.LogsText.ShouldBe("Журнал не прочитан: файл занят");
    }

    [Fact]
    public async Task CreateDiagnosticArchive_ShowsFolderAndNotice()
    {
        // Arrange
        _maintenance.CreateDiagnosticArchiveAsync().Returns(Task.FromResult(@"C:\Data\diagnostics\diag.zip"));
        var page = Create();
        await page.Initialization;

        // Act
        await page.CreateDiagnosticArchiveCommand.ExecuteAsync(null);

        // Assert
        _maintenance.Received(1).OpenFolder(@"C:\Data\diagnostics");
        _notices.Received(1).Show("Диагностический архив собран", @"C:\Data\diagnostics\diag.zip", Arg.Any<FluentAvalonia.UI.Controls.FAInfoBarSeverity>());
    }

    [Fact]
    public async Task ResetSettings_OnlyAfterConfirmation()
    {
        // Arrange
        _maintenance.ResetSettingsAsync().Returns(Task.FromResult<string?>(@"C:\Data\settings.json.bak-1"));
        _dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>()).Returns(Task.FromResult(false), Task.FromResult(true));
        var page = Create();
        await page.Initialization;

        // Act
        await page.ResetSettingsCommand.ExecuteAsync(null);
        var afterDecline = _maintenance.ReceivedCalls().Count(static call => call.GetMethodInfo().Name == nameof(IAppMaintenance.ResetSettingsAsync));
        await page.ResetSettingsCommand.ExecuteAsync(null);

        // Assert
        afterDecline.ShouldBe(0);
        await _maintenance.Received(1).ResetSettingsAsync();
        _notices.Received(1).Show("Настройки сброшены", @"Прежние настройки: C:\Data\settings.json.bak-1", Arg.Any<FluentAvalonia.UI.Controls.FAInfoBarSeverity>());
    }

    [Fact]
    public async Task ErrorReport_ArchiveAndMoreErrors()
    {
        // Arrange
        _maintenance.CreateDiagnosticArchiveAsync().Returns(Task.FromResult(@"C:\Data\diagnostics\diag.zip"));
        var report = new ErrorReportViewModel(_maintenance, "System.Exception: сломалось");
        var closed = false;
        report.CloseRequested += (_, _) => closed = true;

        // Act
        report.AddError("ещё одна");
        await report.CreateArchiveCommand.ExecuteAsync(null);
        report.OpenLogsCommand.Execute(null);
        report.CloseCommand.Execute(null);

        // Assert
        report.Title.ShouldBe("Что-то пошло не так");
        report.MoreText.ShouldBe("Ещё ошибок с тех пор: 1");
        report.ArchivePath.ShouldBe(@"C:\Data\diagnostics\diag.zip");
        report.ArchiveText.ShouldStartWith(@"Архив готов: C:\Data\diagnostics\diag.zip.");
        _maintenance.Received(1).OpenFolder(@"C:\Data\diagnostics");
        _maintenance.Received(1).OpenFolder(@"C:\Data\logs");
        closed.ShouldBeTrue();
    }

    [Fact]
    public async Task ErrorReport_PreviousCrashAndFailedArchive()
    {
        // Arrange
        _maintenance.CreateDiagnosticArchiveAsync().Returns(Task.FromException<string>(new IOException("диск полон")));
        var report = new ErrorReportViewModel(_maintenance, new string('x', 30_000), previousCrash: true);

        // Act
        await report.CreateArchiveCommand.ExecuteAsync(null);

        // Assert
        report.Title.ShouldBe("DiskApp аварийно завершился в прошлый раз");
        report.Details.Length.ShouldBe(20_001, "длинный стек обрезается");
        report.ArchiveText.ShouldBe("Архив не собран: диск полон");
        report.ArchivePath.ShouldBeNull();
    }

    private SettingsViewModel Create() => new(
        Substitute.For<IThemeService>(),
        new SampleSearchService(),
        new HotkeyStatus(),
        settings: new SampleSettingsService(),
        dialogs: _dialogs,
        notices: _notices,
        maintenance: _maintenance);
}

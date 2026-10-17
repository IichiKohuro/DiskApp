using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

using DiskApp.Client;
using DiskApp.Desktop.Platform.Windows;
using DiskApp.Desktop.Shell;
using DiskApp.Desktop.SingleInstance;
using DiskApp.Desktop.Startup;
using DiskApp.Infrastructure;
using DiskApp.Server;
using DiskApp.UI;
using DiskApp.UI.Localization;
using DiskApp.UI.Services;

using Microsoft.Extensions.DependencyInjection;

using Serilog;
using Serilog.Events;

namespace DiskApp.Desktop;

internal static class Program
{
    private static readonly TimeSpan SecondInstanceTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ServerStopTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HubStopTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ServerSlowStartNotice = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Уровень журнала: до чтения настроек — подробный, затем из настройки <c>diagnostics.logLevel</c> без перезапуска.
    /// </summary>
    public static Serilog.Core.LoggingLevelSwitch LogLevel { get; } = new(LogEventLevel.Debug);

    // Код инициализации. До запуска Avalonia нельзя использовать её API, сторонние библиотеки
    // и код, зависящий от SynchronizationContext: платформа ещё не готова.
    [STAThread]
    public static int Main(string[] args)
    {
        var paths = AppPaths.Resolve(args);
        Log.Logger = CreateLogger(paths);
        CrashHandler.Install(paths.DataDirectory);

        try
        {
            Log.Information("DiskApp: Main через {StartupMs} мс после старта процесса; данные: {DataDirectory}; портативный режим: {IsPortable}",
                StartupTimer.Milliseconds, paths.DataDirectory, paths.IsPortable);

            // Один экземпляр на пользователя: второй передаёт аргументы первому и завершается.
            var names = InstanceNames.ForCurrentUser(dataScope: paths.IsPortable ? paths.DataDirectory : null);
            using var instance = SingleInstanceGuard.Acquire(names.Mutex);
            if (!instance.IsFirstInstance)
            {
                var delivered = InstanceClient.TrySendAsync(names.Pipe, args, SecondInstanceTimeout).GetAwaiter().GetResult();
                Log.Information("Экземпляр уже запущен; аргументы {Arguments} {Outcome}", args, delivered ? "переданы ему" : "не доставлены");
                return delivered ? 0 : 1;
            }

            if (StartupArguments.Parse(args).Exit)
            {
                Log.Information("--exit: запущенного экземпляра нет, завершать нечего");
                return 0;
            }

            // Приложение живёт в трее: закрытие окон его не завершает, выход — из меню трея.
            using var lifetime = new ClassicDesktopStyleApplicationLifetime
            {
                Args = args,
                ShutdownMode = ShutdownMode.OnExplicitShutdown,
            };

            // Общий контейнер сервера и оболочки собирается в фоне, пока поднимается Avalonia: на критическом пути трея его нет.
            // Безопасный режим (этап 9.5): индексатор не запускается, поиск работает по готовому индексу.
            var safeMode = StartupArguments.Parse(args).SafeMode;
            if (safeMode)
            {
                Log.Warning("Безопасный режим: без автоиндексации и интеграций");
            }

            var hostCreation = Task.Run(() => CreateServerHost(paths, lifetime, names, safeMode));
            var hardwareAcceleration = RenderingChoice.UseHardwareAcceleration(paths.SettingsPath, Environment.GetEnvironmentVariable(RenderingChoice.Variable));
            RenderingInfo.HardwareAccelerated = hardwareAcceleration;
            Log.Information("Рендеринг: {Rendering}", hardwareAcceleration ? "аппаратный (GPU)" : "программный");
            BuildAvaloniaApp(hardwareAcceleration).SetupWithLifetime(lifetime);
            Log.Information("Avalonia готова через {StartupMs} мс после старта процесса", StartupTimer.Milliseconds);

            var host = hostCreation.GetAwaiter().GetResult();
            try
            {
                var shell = host.Services.GetRequiredService<DesktopShell>();
                CrashHandler.InstallUi(shell.ReportError);
                shell.Start(args);

                // База, индексатор и Kestrel запускаются после трея, в фоне.
                var serverStart = Task.Run(() => StartServerAsync(host));
                var exitCode = lifetime.Start(args);
                StopServer(host, serverStart);
                Log.Information("DiskApp завершён с кодом {ExitCode}", exitCode);
                return exitCode;
            }
            finally
            {
                Task.Run(async () => await host.DisposeAsync().ConfigureAwait(false)).GetAwaiter().GetResult();
            }
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "DiskApp аварийно завершён");
            CrashHandler.WriteMarker(paths.DataDirectory, exception, DateTimeOffset.Now);
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    // Конфигурация Avalonia. Не удалять: метод также использует визуальный конструктор.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(hardwareAcceleration: true);

    // Без аппаратного ускорения окна рисуются программно (ADR-0009): так приложение укладывается в бюджет памяти.
    private static AppBuilder BuildAvaloniaApp(bool hardwareAcceleration)
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect();
        if (!hardwareAcceleration)
        {
            builder = builder.With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] });
        }

        return builder
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
    }

    private static DiskAppServerHost CreateServerHost(AppPaths paths, IClassicDesktopStyleApplicationLifetime lifetime, InstanceNames names, bool safeMode)
    {
        var options = new DiskAppServerOptions
        {
            Paths = paths,
            StartIndexer = !safeMode,
#if DEBUG
            EnvironmentName = "Development",
#endif
        };

        var host = DiskAppServerHost.Create(options, services =>
        {
            services.AddSerilog(Log.Logger);
            services.AddSingleton(Log.Logger);

            // Клиент API для окон: адрес сервера внутри процесса появится после его старта.
            services.AddDiskAppClient();
            services.AddSingleton(lifetime);
            services.AddSingleton(names);
            services.AddDiskAppUi();

            // Окна работают с настоящим индексом через клиент API, а не с макетом.
            services.AddSingleton<ISearchService, ApiSearchService>();
            services.AddSingleton<IFileActions, ApiFileActions>();
            services.AddSingleton<IClipboardService, ShellClipboardService>();
            services.AddSingleton<IIconCache, ApiIconCache>();
            services.AddSingleton<IRootsService, ApiRootsService>();
            services.AddSingleton<IBrowseService, ApiBrowseService>();
            services.AddSingleton<IOperationsService, ApiOperationsService>();
            services.AddSingleton<IAddPreferences, SettingsAddPreferences>();
            services.AddSingleton<IAddFilesService, AddFilesService>();
            services.AddSingleton<ILibraryService, ApiLibraryService>();
            services.AddSingleton<ISettingsService, ApiSettingsService>();
            services.AddSingleton<ISortingSettings, ApiSortingSettings>();
            services.AddSingleton<IInboxService, ApiInboxService>();
            services.AddSingleton<INotesService, ApiNotesService>();
            services.AddSingleton<IFileHistoryService, ApiFileHistoryService>();
            services.AddSingleton<ILayoutStore>(static provider => new Windows.DesktopLayoutStore(
                new Windows.WindowPlacementStore(Path.Combine(provider.GetRequiredService<DiskApp.Core.Abstractions.IAppPaths>().DataDirectory, "window-state.json"))));
            services.AddSingleton<ITagEditor, TagEditor>();
            services.AddSingleton<SettingsTipsService>();
            services.AddSingleton<ITipsService>(static provider => provider.GetRequiredService<SettingsTipsService>());
            services.AddSingleton<DesktopShell>();
            services.AddSingleton<INotificationHost>(static provider => new NotificationCardPresenter(() => provider.GetRequiredService<DesktopShell>().IsMainWindowVisible));
            services.AddSingleton<IReminderStore>(static provider => new JsonReminderStore(Path.Combine(provider.GetRequiredService<DiskApp.Core.Abstractions.IAppPaths>().DataDirectory, "reminders.json")));
            services.AddSingleton<IAppCommands>(static provider => provider.GetRequiredService<DesktopShell>());
            services.AddSingleton<IHotkeyChecker>(static provider => provider.GetRequiredService<DesktopShell>());
            services.AddSingleton<IAppMaintenance>(static provider => new DesktopMaintenance(
                provider.GetRequiredService<DiskApp.Core.Abstractions.IAppPaths>(),
                provider.GetRequiredService<IDiskAppApi>(),
                provider.GetRequiredService<Serilog.ILogger>(),
                provider.GetService<IIconCache>(),
                provider.GetRequiredService<DiskApp.Core.Abstractions.ISettingsStore<DiskApp.Core.Settings.AppSettings>>()));
            services.AddSingleton<IDialogService>(static provider => new DesktopDialogService(provider.GetRequiredService<DesktopShell>().MainWindowForDialogs));
            services.AddSingleton<IAutoStartService>(_ => OperatingSystem.IsWindows() && Environment.ProcessPath is { } executable
                ? new RegistryAutoStartService(executable, paths.IsPortable)
                : new NullAutoStartService());
            services.AddSingleton<IShellIntegrationService>(_ => OperatingSystem.IsWindows() && Environment.ProcessPath is { } executable
                ? new ShellIntegrationService(executable, paths.IsPortable)
                : new NullShellIntegrationService());

            // Настоящие действия оболочки (открыть, Проводник, свойства, корзина) и иконки Windows — только здесь, не в тестовых хостах.
            if (OperatingSystem.IsWindows())
            {
                services.AddSingleton<QuickSwitchController>();
                services.AddSingleton<IQuickSwitchSettings>(static provider => OperatingSystem.IsWindows()
                    ? provider.GetRequiredService<QuickSwitchController>()
                    : new NullQuickSwitchSettings());
                services.AddSingleton<DiskApp.Core.Abstractions.IPlatformShell, DiskApp.Infrastructure.Platform.Windows.WindowsPlatformShell>();
                services.AddSingleton<DiskApp.Core.Abstractions.IIconProvider, DiskApp.Infrastructure.Platform.Windows.WindowsIconProvider>();
                services.AddSingleton<DiskApp.Core.Abstractions.IFileLockInspector, DiskApp.Infrastructure.Platform.Windows.WindowsFileLockInspector>();
                services.AddSingleton<DiskApp.Core.Abstractions.IRecycleBin, DiskApp.Infrastructure.Platform.Windows.WindowsRecycleBin>();
                services.AddSingleton<DesktopResumeSource>();
                services.AddSingleton<WindowsUserActivityMonitor>();
                services.AddSingleton<IUserActivityMonitor>(static provider => OperatingSystem.IsWindows()
                    ? provider.GetRequiredService<WindowsUserActivityMonitor>()
                    : new NullUserActivityMonitor());
                services.AddSingleton<DiskApp.Core.Abstractions.ISystemResumeSource>(static provider => OperatingSystem.IsWindows()
                    ? provider.GetRequiredService<DesktopResumeSource>()
                    : throw new PlatformNotSupportedException());
            }
        });
        Log.Information("Контейнер сервера и оболочки собран через {StartupMs} мс после старта процесса", StartupTimer.Milliseconds);
        return host;
    }

    private static async Task StartServerAsync(DiskAppServerHost host)
    {
        var endpoint = host.Services.GetRequiredService<InProcessServerEndpointProvider>();
        var dispatcher = host.Services.GetRequiredService<IDispatcher>();
        var notices = host.Services.GetRequiredService<IInfoBarService>();

        // Обычно сервер готов меньше чем за секунду; если дольше (большая база проверяется на целостность) — сказать об этом.
        using var slow = new Timer(
            _ => dispatcher.Post(() => notices.Show(L.T("Program.TheServerIsTakingLongerThan"), L.T("Program.TheIndexDatabaseIsStillBeing"))),
            null,
            ServerSlowStartNotice,
            Timeout.InfiniteTimeSpan);
        try
        {
            await host.StartAsync().ConfigureAwait(false);
            endpoint.SetEndpoint(new ServerEndpoint(host.BaseAddress!));
            Log.Information("Сервер слушает {Address}: готов через {StartupMs} мс после старта процесса", host.BaseAddress, StartupTimer.Milliseconds);
        }
        catch (Exception exception)
        {
            // Без сервера интерфейс работает на макетных данных; причина — в журнале и в уведомлении, запросы клиента получат ошибку.
            endpoint.Fail(exception);
            Log.Error(exception, "Сервер не запустился");
            dispatcher.Post(() => notices.Show(L.T("Program.TheDiskAppServerDidNotStart"), exception.Message + L.T("Program.DetailsAreInTheLog"), FluentAvalonia.UI.Controls.FAInfoBarSeverity.Error));
        }
    }

    private static void StopServer(DiskAppServerHost host, Task serverStart)
    {
        // Вне потока интерфейса: продолжения не должны ждать остановленный диспетчер Avalonia.
        Task.Run(async () =>
        {
            await serverStart.ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(ServerStopTimeout);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            Log.Information("Остановка сервера");

            // Хаб закрывается первым: иначе он переподключается к останавливающемуся серверу (FEEDBACK.md, 2026-10-14).
            try
            {
                await host.Services.GetRequiredService<DiskAppHubConnection>().DisposeAsync().AsTask()
                    .WaitAsync(HubStopTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Log.Warning("Хаб не закрылся за {Timeout}", HubStopTimeout);
            }

            try
            {
                await host.StopAsync(timeout.Token).ConfigureAwait(false);
                Log.Information("Сервер остановлен за {ElapsedMs} мс", stopwatch.ElapsedMilliseconds);
            }
            catch (OperationCanceledException)
            {
                Log.Warning("Сервер не остановился за {Timeout}", ServerStopTimeout);
            }
        }).GetAwaiter().GetResult();
    }

    private static Serilog.Core.Logger CreateLogger(AppPaths paths)
        => new LoggerConfiguration()
            .MinimumLevel.ControlledBy(LogLevel)

            // Запросы клиента к серверу внутри процесса, их повторы и обработка в Kestrel давали по десятку строк на запрос
            // и вытесняли из журнала события самого приложения (страница журнала, этап 9.5): от них — только предупреждения.
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
            .MinimumLevel.Override("Polly", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, "diskapp-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                formatProvider: CultureInfo.InvariantCulture,
                outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
}

using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

using DiskApp.Desktop.Shell;
using DiskApp.Desktop.SingleInstance;
using DiskApp.Desktop.Startup;
using DiskApp.UI;
using DiskApp.UI.Services;

using Microsoft.Extensions.DependencyInjection;

using Serilog;
using Serilog.Events;

namespace DiskApp.Desktop;

internal static class Program
{
    private static readonly TimeSpan SecondInstanceTimeout = TimeSpan.FromSeconds(3);

    // Код инициализации. До запуска Avalonia нельзя использовать её API, сторонние библиотеки
    // и код, зависящий от SynchronizationContext: платформа ещё не готова.
    [STAThread]
    public static int Main(string[] args)
    {
        var paths = AppPaths.Resolve(args);
        Log.Logger = CreateLogger(paths);

        try
        {
            Log.Information("DiskApp: Main через {StartupMs} мс после старта процесса; данные: {DataDirectory}; портативный режим: {IsPortable}",
                StartupTimer.Milliseconds, paths.DataDirectory, paths.IsPortable);

            // Один экземпляр на пользователя: второй передаёт аргументы первому и завершается.
            var names = InstanceNames.ForCurrentUser();
            using var instance = SingleInstanceGuard.Acquire(names.Mutex);
            if (!instance.IsFirstInstance)
            {
                var delivered = InstanceClient.TrySendAsync(names.Pipe, args, SecondInstanceTimeout).GetAwaiter().GetResult();
                Log.Information("Экземпляр уже запущен; аргументы {Arguments} {Outcome}", args, delivered ? "переданы ему" : "не доставлены");
                return delivered ? 0 : 1;
            }

            // Приложение живёт в трее: закрытие окон его не завершает, выход — из меню трея.
            using var lifetime = new ClassicDesktopStyleApplicationLifetime
            {
                Args = args,
                ShutdownMode = ShutdownMode.OnExplicitShutdown,
            };

            BuildAvaloniaApp().SetupWithLifetime(lifetime);
            Log.Information("Avalonia готова через {StartupMs} мс после старта процесса", StartupTimer.Milliseconds);

            using var services = BuildServices(lifetime, names);
            var shell = services.GetRequiredService<DesktopShell>();
            shell.Start(args);

            var exitCode = lifetime.Start(args);
            Log.Information("DiskApp завершён с кодом {ExitCode}", exitCode);
            return exitCode;
        }
        catch (Exception exception)
        {
            Log.Fatal(exception, "DiskApp аварийно завершён");
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    // Конфигурация Avalonia. Не удалять: метод также использует визуальный конструктор.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

    private static ServiceProvider BuildServices(IClassicDesktopStyleApplicationLifetime lifetime, InstanceNames names)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Log.Logger);
        services.AddSingleton(lifetime);
        services.AddSingleton(names);
        services.AddDiskAppUi();
        services.AddSingleton<DesktopShell>();
        services.AddSingleton<IAppCommands>(static provider => provider.GetRequiredService<DesktopShell>());
        return services.BuildServiceProvider();
    }

    private static Serilog.Core.Logger CreateLogger(AppPaths paths)
        => new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
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

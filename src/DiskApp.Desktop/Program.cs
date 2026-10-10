using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

using DiskApp.Desktop.Shell;
using DiskApp.UI;
using DiskApp.UI.Services;

using Microsoft.Extensions.DependencyInjection;

using Serilog;
using Serilog.Events;

namespace DiskApp.Desktop;

internal static class Program
{
    // Код инициализации. До запуска Avalonia нельзя использовать её API, сторонние библиотеки
    // и код, зависящий от SynchronizationContext: платформа ещё не готова.
    [STAThread]
    public static int Main(string[] args)
    {
        var paths = AppPaths.Resolve(args);
        Log.Logger = CreateLogger(paths);

        try
        {
            Log.Information("DiskApp запускается; данные: {DataDirectory}; портативный режим: {IsPortable}", paths.DataDirectory, paths.IsPortable);

            // Приложение живёт в трее: закрытие окон его не завершает, выход — из меню трея.
            using var lifetime = new ClassicDesktopStyleApplicationLifetime
            {
                Args = args,
                ShutdownMode = ShutdownMode.OnExplicitShutdown,
            };

            BuildAvaloniaApp().SetupWithLifetime(lifetime);

            using var services = BuildServices(lifetime);
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

    private static ServiceProvider BuildServices(IClassicDesktopStyleApplicationLifetime lifetime)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Log.Logger);
        services.AddSingleton(lifetime);
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
                formatProvider: CultureInfo.InvariantCulture,
                outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

using DiskApp.UI;

namespace DiskApp.Desktop;

internal static class Program
{
    // Код инициализации. До запуска Avalonia нельзя использовать её API, сторонние библиотеки
    // и код, зависящий от SynchronizationContext: платформа ещё не готова.
    [STAThread]
    public static int Main(string[] args)
    {
        // Время жизни создаётся явно: окна принадлежат десктопу, а не общему App из DiskApp.UI.
        // Режим завершения сменится на OnExplicitShutdown, когда появится трей (этап 6.3).
        using var lifetime = new ClassicDesktopStyleApplicationLifetime
        {
            Args = args,
            ShutdownMode = ShutdownMode.OnMainWindowClose,
        };

        BuildAvaloniaApp().SetupWithLifetime(lifetime);
        lifetime.MainWindow = new MainWindow();

        return lifetime.Start(args);
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
}

using System.Globalization;
using System.Text;

using Avalonia.Threading;

using Serilog;

namespace DiskApp.Desktop.Startup;

/// <summary>
/// Глобальные обработчики ошибок (PLAN.md, этап 9.5). Ошибка в потоке интерфейса и ненаблюдаемое исключение задачи
/// пишутся в журнал и показываются окном «Что-то пошло не так», приложение продолжает работу. Необработанное исключение
/// домена завершает процесс: оно пишется в журнал и в файл-отметку, по которой следующий запуск покажет то же окно.
/// </summary>
internal static class CrashHandler
{
    /// <summary>Файл-отметка аварийного завершения в папке данных.</summary>
    public const string MarkerFileName = "last-crash.txt";

    private static string? _dataDirectory;
    private static Action<Exception>? _report;
    private static int _reporting;

    /// <summary>
    /// Подключает обработчики домена и задач; вызывать сразу после создания журнала.
    /// </summary>
    public static void Install(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _dataDirectory = dataDirectory;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>
    /// Подключает обработчик потока интерфейса и того, кто показывает окно ошибки; вызывать после настройки Avalonia.
    /// </summary>
    public static void InstallUi(Action<Exception> report)
    {
        ArgumentNullException.ThrowIfNull(report);
        _report = report;
        Dispatcher.UIThread.UnhandledException += OnDispatcherException;
    }

    /// <summary>Записывает отметку аварийного завершения.</summary>
    public static void WriteMarker(string dataDirectory, Exception exception, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(exception);
        try
        {
            Directory.CreateDirectory(dataDirectory);
            var text = new StringBuilder()
                .Append(now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)).AppendLine()
                .Append(exception).AppendLine()
                .ToString();
            File.WriteAllText(Path.Combine(dataDirectory, MarkerFileName), text, Encoding.UTF8);
        }
        catch (Exception markerException) when (markerException is IOException or UnauthorizedAccessException)
        {
            Log.Warning(markerException, "Отметка аварийного завершения не записана");
        }
    }

    /// <summary>Читает и удаляет отметку прошлого аварийного завершения; <c>null</c> — завершение было штатным.</summary>
    public static string? TakeMarker(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, MarkerFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            File.Delete(path);
            return text;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warning(exception, "Отметка аварийного завершения не прочитана");
            return null;
        }
    }

    private static void OnDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString());
        Log.Fatal(exception, "Необработанное исключение; процесс завершается: {Terminating}", e.IsTerminating);
        if (_dataDirectory is { } directory)
        {
            WriteMarker(directory, exception, DateTimeOffset.Now);
        }

        Log.CloseAndFlush();
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Ненаблюдаемое исключение фоновой задачи");
        e.SetObserved();
        Report(e.Exception.InnerExceptions.Count == 1 ? e.Exception.InnerExceptions[0] : e.Exception);
    }

    private static void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Необработанное исключение в потоке интерфейса");
        e.Handled = true;
        Report(e.Exception);
    }

    // Окно показывается на потоке интерфейса; ошибка при его показе не порождает новое окно.
    private static void Report(Exception exception)
    {
        if (_report is not { } report)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (Interlocked.Exchange(ref _reporting, 1) == 1)
            {
                return;
            }

            try
            {
                report(exception);
            }
            catch (Exception reportException)
            {
                Log.Error(reportException, "Окно ошибки не показано");
            }
            finally
            {
                Interlocked.Exchange(ref _reporting, 0);
            }
        });
    }
}

using System.Diagnostics;

namespace DiskApp.Desktop.Startup;

/// <summary>
/// Время с момента запуска процесса — для журнала старта (бюджет: трей менее чем за 1 с при тёплом старте).
/// </summary>
internal static class StartupTimer
{
    private static readonly DateTime ProcessStart = Process.GetCurrentProcess().StartTime;

    /// <summary>Сколько прошло с запуска процесса.</summary>
    public static TimeSpan SinceProcessStart => DateTime.Now - ProcessStart;

    /// <summary>Миллисекунды с запуска процесса, округлённые для журнала.</summary>
    public static long Milliseconds => (long)SinceProcessStart.TotalMilliseconds;
}

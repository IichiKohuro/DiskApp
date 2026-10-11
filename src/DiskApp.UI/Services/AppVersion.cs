using System.Reflection;

namespace DiskApp.UI.Services;

/// <summary>
/// Версия приложения без суффикса сборки: «0.5.0» или «0.5.0-alpha».
/// </summary>
public static class AppVersion
{
    /// <summary>Версия запущенной сборки.</summary>
    public static string Current { get; } = Read();

    private static string Read()
    {
        var assembly = typeof(AppVersion).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
        {
            return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? informational[..plus] : informational;
    }
}

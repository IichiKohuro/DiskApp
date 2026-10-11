using System.Runtime.Versioning;
using System.Security;

using DiskApp.UI.Services;

using Microsoft.Win32;

namespace DiskApp.Desktop.Platform.Windows;

/// <summary>
/// Автозапуск через <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> (PLAN.md, этап 6.6): значение
/// <c>"&lt;путь&gt;\DiskApp.exe" --minimized</c> (у портативной копии ещё <c>--portable</c>). Включённым считается
/// только значение, указывающее на этот исполняемый файл: после переноса копии переключатель покажет «выключено».
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class RegistryAutoStartService : IAutoStartService
{
    /// <summary>Ключ автозапуска текущего пользователя.</summary>
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _executable;
    private readonly bool _portable;
    private readonly string _keyPath;
    private readonly string _valueName;

    /// <summary>
    /// Создаёт службу; тесты передают свой ключ, чтобы не трогать настоящий автозапуск.
    /// </summary>
    public RegistryAutoStartService(string executable, bool portable, string keyPath = RunKeyPath, string valueName = "DiskApp")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);

        _executable = Path.GetFullPath(executable);
        _portable = portable;
        _keyPath = keyPath;
        _valueName = valueName;
    }

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
            return key?.GetValue(_valueName) is string command && string.Equals(ExecutableOf(command), _executable, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <inheritdoc />
    public string Warning
        => _portable ? $"Портативная копия запускается из «{Path.GetDirectoryName(_executable)}»: если папку перенести, автозапуск нужно включить заново." : string.Empty;

    /// <summary>Команда, которая пишется в реестр.</summary>
    public string Command => $"\"{_executable}\" --minimized" + (_portable ? " --portable" : string.Empty);

    /// <inheritdoc />
    public void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
            if (enabled)
            {
                key.SetValue(_valueName, Command, RegistryValueKind.String);
            }
            else if (key.GetValue(_valueName) is string command && string.Equals(ExecutableOf(command), _executable, StringComparison.OrdinalIgnoreCase))
            {
                // Чужое значение с тем же именем (другая копия) не удаляется.
                key.DeleteValue(_valueName, throwOnMissingValue: false);
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException)
        {
            throw new InvalidOperationException("Windows не дала изменить автозапуск: " + exception.Message, exception);
        }
    }

    private static string ExecutableOf(string command)
    {
        var trimmed = command.Trim();
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            return end > 1 ? trimmed[1..end] : trimmed.Trim('"');
        }

        var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        return space > 0 ? trimmed[..space] : trimmed;
    }
}

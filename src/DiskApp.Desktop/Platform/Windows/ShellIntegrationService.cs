using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;

using DiskApp.UI.Localization;
using DiskApp.UI.Services;

using Microsoft.Win32;

namespace DiskApp.Desktop.Platform.Windows;

/// <summary>
/// Интеграции с Проводником (PLAN.md, этап 8.5), всё в профиле пользователя без прав администратора:
/// <list type="bullet">
/// <item>ярлык <c>DiskApp.lnk</c> в <c>shell:sendto</c> — <c>--import</c> со всеми выбранными файлами;</item>
/// <item>контекстное меню: для файлов «Добавить в DiskApp» (<c>--import</c>), для папок «Добавить в индекс DiskApp»
/// (<c>--add</c>) и «Открыть в DiskApp» (<c>--page browse --path</c>), для фона папки — «Открыть в DiskApp»;</item>
/// <item>протокол <c>diskapp://</c>.</item>
/// </list>
/// Включённой считается интеграция, которая указывает на этот исполняемый файл.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ShellIntegrationService : IShellIntegrationService
{
    /// <summary>Классы текущего пользователя.</summary>
    public const string ClassesKeyPath = @"Software\Classes";

    private const string AddVerb = @"*\shell\DiskApp.Add";
    private const string IndexVerb = @"Directory\shell\DiskApp.Index";
    private const string OpenVerb = @"Directory\shell\DiskApp.Open";
    private const string BackgroundVerb = @"Directory\Background\shell\DiskApp.Open";
    private const string ProtocolKey = "diskapp";
    private const string ProtocolCommand = ProtocolKey + @"\shell\open\command";

    private readonly string _executable;
    private readonly bool _portable;
    private readonly string _classesKeyPath;
    private readonly string _sendToFolder;
    private readonly string _desktopFolder;

    /// <summary>
    /// Создаёт службу; тесты передают свой ключ классов и папки «Отправить» и рабочего стола, чтобы не трогать настоящие.
    /// </summary>
    public ShellIntegrationService(string executable, bool portable, string classesKeyPath = ClassesKeyPath, string? sendToFolder = null, string? desktopFolder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        _executable = Path.GetFullPath(executable);
        _portable = portable;
        _classesKeyPath = classesKeyPath;
        _sendToFolder = sendToFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.SendTo);
        _desktopFolder = desktopFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <summary>Путь ярлыка в «Отправить».</summary>
    public string SendToShortcut => Path.Combine(_sendToFolder, "DiskApp.lnk");

    /// <summary>Путь ярлыка на рабочем столе.</summary>
    public string DesktopShortcut => Path.Combine(_desktopFolder, "DiskApp.lnk");

    /// <inheritdoc />
    public bool IsEnabled(ShellIntegrationFeature feature) => feature switch
    {
        ShellIntegrationFeature.SendTo => PointsHere(SendToShortcut),
        ShellIntegrationFeature.DesktopShortcut => PointsHere(DesktopShortcut),
        ShellIntegrationFeature.ContextMenu => CommandPointsHere(AddVerb + @"\command"),
        _ => CommandPointsHere(ProtocolCommand),
    };

    /// <inheritdoc />
    public void SetEnabled(ShellIntegrationFeature feature, bool enabled)
    {
        try
        {
            switch (feature)
            {
                case ShellIntegrationFeature.SendTo:
                    SetShortcut(SendToShortcut, "--import", L.T("ShellIntegration.PutFilesIntoADiskAppFolder"), enabled);
                    break;
                case ShellIntegrationFeature.DesktopShortcut:
                    SetShortcut(DesktopShortcut, "--show", L.T("ShellIntegration.SearchAndSortFiles"), enabled);
                    break;
                case ShellIntegrationFeature.ContextMenu:
                    SetContextMenu(enabled);
                    break;
                default:
                    SetProtocol(enabled);
                    break;
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException or COMException)
        {
            throw new InvalidOperationException(L.T("ShellIntegration.WindowsDidNotAllowChangingThe") + exception.Message, exception);
        }
    }

    /// <summary>Команда запуска с аргументами (у портативной копии — с <c>--portable</c>).</summary>
    public string Command(string arguments) => $"\"{_executable}\"" + (_portable ? " --portable" : string.Empty) + " " + arguments;

    private static string? ExecutableOf(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }

        var space = command.IndexOf(' ', StringComparison.Ordinal);
        return space > 0 ? command[..space] : command;
    }

    private bool SameExecutable(string path) => string.Equals(Path.GetFullPath(path), _executable, StringComparison.OrdinalIgnoreCase);

    private bool CommandPointsHere(string commandKey)
    {
        using var command = Registry.CurrentUser.OpenSubKey($@"{_classesKeyPath}\{commandKey}");
        return command?.GetValue(null) is string value && ExecutableOf(value) is { } executable && SameExecutable(executable);
    }

    private void WriteVerb(string key, string title, string arguments)
    {
        using var verb = Registry.CurrentUser.CreateSubKey($@"{_classesKeyPath}\{key}", writable: true);
        verb.SetValue(null, title);
        verb.SetValue("Icon", $"\"{_executable}\",0");
        using var command = verb.CreateSubKey("command", writable: true);
        command.SetValue(null, Command(arguments));
    }

    private void DeleteIfOurs(string key)
    {
        using var command = Registry.CurrentUser.OpenSubKey($@"{_classesKeyPath}\{key}\command");
        var ours = command?.GetValue(null) is string value && ExecutableOf(value) is { } executable && SameExecutable(executable);
        command?.Dispose();
        if (ours)
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"{_classesKeyPath}\{key}", throwOnMissingSubKey: false);
        }
    }

    private void SetContextMenu(bool enabled)
    {
        if (enabled)
        {
            WriteVerb(AddVerb, L.T("ShellIntegration.AddToDiskApp"), "--import \"%1\"");
            WriteVerb(IndexVerb, L.T("ShellIntegration.AddToDiskAppIndex"), "--add \"%1\"");
            WriteVerb(OpenVerb, L.T("ShellIntegration.OpenInDiskApp"), "--page browse --path \"%1\"");
            WriteVerb(BackgroundVerb, L.T("ShellIntegration.OpenInDiskApp"), "--page browse --path \"%V\"");
            return;
        }

        foreach (var key in new[] { AddVerb, IndexVerb, OpenVerb, BackgroundVerb })
        {
            DeleteIfOurs(key);
        }
    }

    private void SetProtocol(bool enabled)
    {
        var root = $@"{_classesKeyPath}\{ProtocolKey}";
        if (!enabled)
        {
            if (CommandPointsHere(ProtocolCommand))
            {
                Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
            }

            return;
        }

        using var protocol = Registry.CurrentUser.CreateSubKey(root, writable: true);
        protocol.SetValue(null, "URL:DiskApp");
        protocol.SetValue("URL Protocol", string.Empty);
        using (var icon = protocol.CreateSubKey("DefaultIcon", writable: true))
        {
            icon.SetValue(null, $"\"{_executable}\",0");
        }

        using var command = protocol.CreateSubKey(@"shell\open\command", writable: true);
        command.SetValue(null, Command("\"%1\""));
    }

    private bool PointsHere(string shortcut) => File.Exists(shortcut) && ShortcutTarget(shortcut) is { } target && SameExecutable(target);

    /// <summary>
    /// Создаёт ярлык на эту копию или удаляет его; чужой ярлык с тем же именем (другая копия DiskApp) не удаляется.
    /// </summary>
    private void SetShortcut(string shortcut, string arguments, string description, bool enabled)
    {
        if (!enabled)
        {
            if (PointsHere(shortcut))
            {
                File.Delete(shortcut);
            }

            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(shortcut)!);
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(_executable);
            link.SetArguments((_portable ? "--portable " : string.Empty) + arguments);
            link.SetDescription(description);
            link.SetIconLocation(_executable, 0);
            ((IPersistFile)link).Save(shortcut, fRemember: true);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    private static string? ShortcutTarget(string shortcut)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(shortcut, 0);
            var buffer = new char[1024];
            link.GetPath(buffer, buffer.Length, 0, 0);
            var path = new string(buffer).TrimEnd('\0');
            return path.Length > 0 ? path : null;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] file, int maxPath, nint findData, int flags);

        void GetIDList(out nint pidl);

        void SetIDList(nint pidl);

        void GetDescription([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] name, int maxName);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] dir, int maxPath);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);

        void GetArguments([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] args, int maxPath);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);

        void GetHotkey(out short hotkey);

        void SetHotkey(short hotkey);

        void GetShowCmd(out int showCmd);

        void SetShowCmd(int showCmd);

        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] iconPath, int maxIconPath, out int icon);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pathRel, int reserved);

        void Resolve(nint window, int flags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);

        [PreserveSig]
        int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, int mode);

        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);

        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);

        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }
}

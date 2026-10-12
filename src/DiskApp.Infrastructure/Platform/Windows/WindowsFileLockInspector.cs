using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using DiskApp.Core.Abstractions;

namespace DiskApp.Infrastructure.Platform.Windows;

/// <summary>
/// Кто держит файл — через диспетчер перезапуска Windows (Restart Manager): тот же источник, которым пользуется
/// установщик, когда просит закрыть программы. Возвращает названия приложений, например «Microsoft Excel».
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFileLockInspector : IFileLockInspector
{
    private const int ErrorMoreData = 234;
    private const int MaxApplications = 8;

    /// <inheritdoc />
    public IReadOnlyList<string> FindLockingApplications(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        try
        {
            if (RmStartSession(out var session, 0, Guid.NewGuid().ToString("N")) != 0)
            {
                return [];
            }

            try
            {
                if (RmRegisterResources(session, 1, [path], 0, IntPtr.Zero, 0, null) != 0)
                {
                    return [];
                }

                uint count = 0;
                uint reasons = 0;
                var result = RmGetList(session, out var needed, ref count, null, ref reasons);
                if (result != ErrorMoreData || needed == 0)
                {
                    return [];
                }

                var processes = new RmProcessInfo[Math.Min(needed, MaxApplications)];
                count = (uint)processes.Length;
                if (RmGetList(session, out _, ref count, processes, ref reasons) != 0 && count == 0)
                {
                    return [];
                }

                return [.. processes.Take((int)count)
                    .Select(static process => process.ApplicationName?.Trim() ?? string.Empty)
                    .Where(static name => name.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)];
            }
            finally
            {
                _ = RmEndSession(session);
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            return [];
        }
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RmStartSession(out uint session, int flags, string key);

    [DllImport("rstrtmgr.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RmEndSession(uint session);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RmRegisterResources(uint session, uint fileCount, string[] files, uint applicationCount, IntPtr applications, uint serviceCount, string[]? services);

    [DllImport("rstrtmgr.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RmProcessInfo[]? processes, ref uint rebootReasons);

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME StartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string ApplicationName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string ServiceShortName;

        public int ApplicationType;
        public uint Status;
        public uint SessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;
    }
}

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DiskApp.Infrastructure.Platform.Windows;

/// <summary>
/// Вызовы оболочки Windows для действий над файлами (PLAN.md, этап 7.3).
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class ShellNative
{
    public const uint FoDelete = 0x0003;
    public const ushort FofSilent = 0x0004;
    public const ushort FofNoConfirmation = 0x0010;
    public const ushort FofAllowUndo = 0x0040;
    public const ushort FofNoErrorUi = 0x0400;
    public const ushort FofWantNukeWarning = 0x4000;

    public const uint ShopFilePath = 0x00000002;

    public const int OaifAllowRegistration = 0x00000001;
    public const int OaifRegisterExt = 0x00000002;
    public const int OaifExec = 0x00000004;

    public const uint CfUnicodeText = 13;
    public const uint CfHdrop = 15;
    public const uint GmemMoveable = 0x0002;
    public const uint GmemZeroInit = 0x0040;

    /// <summary>HRESULT_FROM_WIN32(ERROR_CANCELLED): пользователь закрыл диалог.</summary>
    public const int HresultCancelled = unchecked((int)0x800704C7);

    [StructLayout(LayoutKind.Sequential)]
    public struct ShFileOpStruct
    {
        public nint Window;
        public uint Function;
        public nint From;
        public nint To;
        public ushort Flags;
        public int AnyOperationsAborted;
        public nint NameMappings;
        public nint ProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OpenAsInfo
    {
        public nint File;
        public nint Class;
        public int Flags;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    public static partial int ShFileOperation(ref ShFileOpStruct operation);

    [LibraryImport("shell32.dll", EntryPoint = "SHObjectProperties", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShObjectProperties(nint window, uint type, string name, string? page);

    [LibraryImport("shell32.dll", EntryPoint = "SHOpenWithDialog")]
    public static partial int ShOpenWithDialog(nint window, ref OpenAsInfo info);

    [LibraryImport("shell32.dll", EntryPoint = "SHParseDisplayName", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int ShParseDisplayName(string name, nint bindContext, out nint pidl, uint attributesIn, out uint attributesOut);

    [LibraryImport("shell32.dll", EntryPoint = "SHOpenFolderAndSelectItems")]
    public static partial int ShOpenFolderAndSelectItems(nint folder, uint count, nint items, uint flags);

    [LibraryImport("shell32.dll", EntryPoint = "ILFree")]
    public static partial void IlFree(nint pidl);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenClipboard(nint owner);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint SetClipboardData(uint format, nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint GlobalLock(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(nint memory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint GlobalFree(nint memory);

    /// <summary>
    /// Список путей для <c>SHFileOperation</c>: каждый путь завершается нулём, весь список — ещё одним нулём.
    /// </summary>
    public static string DoubleNullList(IEnumerable<string> paths) => string.Concat(paths.Select(static path => path + '\0')) + '\0';

    /// <summary>
    /// Содержимое <c>CF_HDROP</c>: заголовок <c>DROPFILES</c> (20 байт, пути в UTF-16) и список путей с двойным нулём.
    /// </summary>
    public static byte[] DropFiles(IReadOnlyList<string> paths)
    {
        const int headerSize = 20;
        var list = System.Text.Encoding.Unicode.GetBytes(DoubleNullList(paths));
        var data = new byte[headerSize + list.Length];
        BitConverter.TryWriteBytes(data.AsSpan(0, 4), headerSize);
        BitConverter.TryWriteBytes(data.AsSpan(16, 4), 1);
        list.CopyTo(data, headerSize);
        return data;
    }
}

using System.Runtime.InteropServices;

namespace DiskApp.Desktop.Platform.Windows;

/// <summary>
/// Функции Win32 для Quick Switch (PLAN.md, этап 8.6, ADR-0008): поиск окон и обмен текстом с элементами чужого
/// диалога. Сообщения <c>WM_GETTEXT</c> и <c>WM_SETTEXT</c> Windows сама переносит между процессами.
/// </summary>
internal static unsafe partial class NativeMethods
{
    /// <summary><c>WM_SETTEXT</c>.</summary>
    public const uint WmSetText = 0x000C;

    /// <summary><c>WM_GETTEXT</c>.</summary>
    public const uint WmGetText = 0x000D;

    /// <summary><c>WM_GETTEXTLENGTH</c>.</summary>
    public const uint WmGetTextLength = 0x000E;

    /// <summary><c>WM_COMMAND</c>.</summary>
    public const uint WmCommand = 0x0111;

    /// <summary>Идентификатор кнопки «ОК» диалога (<c>IDOK</c>): в диалогах файлов это «Сохранить» или «Открыть».</summary>
    public const int IdOk = 1;

    /// <summary>Не ждать зависшее окно (<c>SMTO_ABORTIFHUNG</c>).</summary>
    public const uint SmtoAbortIfHung = 0x0002;

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial int GetClassNameW(nint window, char* className, int maxCount);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindowExW(nint parent, nint childAfter, string? className, string? windowName);

    [LibraryImport("user32.dll")]
    public static partial int GetDlgCtrlID(nint window);

    [LibraryImport("user32.dll")]
    public static partial nint GetDlgItem(nint dialog, int id);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint SendMessageTimeoutW(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);

    /// <summary>Имя класса окна или пустая строка.</summary>
    public static string GetClassName(nint window)
    {
        var buffer = stackalloc char[256];
        var length = GetClassNameW(window, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }
}

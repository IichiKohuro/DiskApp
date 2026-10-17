using System.Runtime.InteropServices;

namespace DiskApp.Desktop.Platform.Windows;

/// <summary>Функции Win32 для размещения всплывающих окон Avalonia на нужном мониторе (<see cref="PopupMonitorPlacement"/>).</summary>
internal static unsafe partial class NativeMethods
{
    /// <summary>Хук оконных событий потока (<c>WH_CBT</c>).</summary>
    public const int WhCbt = 5;

    /// <summary>Код <c>HCBT_CREATEWND</c>: окно вот-вот будет создано, <c>CREATESTRUCT</c> ещё можно поменять.</summary>
    public const int HcbtCreateWnd = 3;

    /// <summary>Стиль <c>WS_POPUP</c>.</summary>
    public const uint WsPopup = 0x80000000;

    /// <summary>Расширенный стиль <c>WS_EX_TOOLWINDOW</c>.</summary>
    public const uint WsExToolWindow = 0x00000080;

    /// <summary>Ближайший монитор, если точка вне всех (<c>MONITOR_DEFAULTTONEAREST</c>).</summary>
    public const uint MonitorDefaultToNearest = 2;

    /// <summary>Параметры создаваемого окна (<c>CREATESTRUCTW</c>).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CreateStruct
    {
        /// <summary>Параметр создания.</summary>
        public nint CreateParams;

        /// <summary>Экземпляр модуля.</summary>
        public nint Instance;

        /// <summary>Меню.</summary>
        public nint Menu;

        /// <summary>Родитель или владелец.</summary>
        public nint Parent;

        /// <summary>Высота.</summary>
        public int Height;

        /// <summary>Ширина.</summary>
        public int Width;

        /// <summary>Y.</summary>
        public int Y;

        /// <summary>X.</summary>
        public int X;

        /// <summary>Стиль.</summary>
        public uint Style;

        /// <summary>Заголовок.</summary>
        public char* Name;

        /// <summary>Имя класса или атом.</summary>
        public char* ClassName;

        /// <summary>Расширенный стиль.</summary>
        public uint ExStyle;
    }

    /// <summary>Параметр хука при <see cref="HcbtCreateWnd"/> (<c>CBT_CREATEWNDW</c>).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CbtCreateWnd
    {
        /// <summary>Параметры создаваемого окна.</summary>
        public CreateStruct* CreateStruct;

        /// <summary>Окно, за которым встанет новое по Z-порядку.</summary>
        public nint InsertAfter;
    }

    /// <summary>Сведения о мониторе (<c>MONITORINFO</c>).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfo
    {
        /// <summary>Размер структуры.</summary>
        public uint Size;

        /// <summary>Границы монитора.</summary>
        public Rect Monitor;

        /// <summary>Рабочая область.</summary>
        public Rect Work;

        /// <summary>Флаги.</summary>
        public uint Flags;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint SetWindowsHookExW(int hookId, delegate* unmanaged<int, nint, nint, nint> procedure, nint module, uint threadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(nint hook);

    [LibraryImport("user32.dll")]
    public static partial nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromPoint(Point point, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();
}

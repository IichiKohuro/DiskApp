using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DiskApp.Desktop.Platform.Windows;

/// <summary>
/// Функции Win32 для десктопной оболочки: глобальные горячие клавиши, служебное окно сообщений,
/// положение курсора, скруглённые углы окон через DWM.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class NativeMethods
{
    /// <summary>Сообщение о нажатии зарегистрированной горячей клавиши.</summary>
    public const uint WmHotkey = 0x0312;

    /// <summary>Модификатор Alt для <see cref="RegisterHotKey"/>.</summary>
    public const uint ModAlt = 0x0001;

    /// <summary>Модификатор Ctrl.</summary>
    public const uint ModControl = 0x0002;

    /// <summary>Модификатор Shift.</summary>
    public const uint ModShift = 0x0004;

    /// <summary>Модификатор Win.</summary>
    public const uint ModWin = 0x0008;

    /// <summary>Не повторять сообщение при удержании клавиши.</summary>
    public const uint ModNoRepeat = 0x4000;

    /// <summary>Виртуальный код клавиши «пробел».</summary>
    public const uint VkSpace = 0x20;

    /// <summary>Виртуальный код клавиши D.</summary>
    public const uint VkD = 0x44;

    /// <summary>Виртуальный код клавиши F.</summary>
    public const uint VkF = 0x46;

    /// <summary>Родитель для окна, которое только принимает сообщения (<c>HWND_MESSAGE</c>).</summary>
    public const nint HwndMessage = -3;

    /// <summary>Атрибут DWM «предпочтение формы углов» (<c>DWMWA_WINDOW_CORNER_PREFERENCE</c>, Windows 11).</summary>
    public const int DwmwaWindowCornerPreference = 33;

    /// <summary>Значение атрибута: скруглённые углы (<c>DWMWCP_ROUND</c>).</summary>
    public const int DwmwcpRound = 2;

    /// <summary>Код ошибки «горячая клавиша уже зарегистрирована» (<c>ERROR_HOTKEY_ALREADY_REGISTERED</c>).</summary>
    public const int ErrorHotkeyAlreadyRegistered = 1409;

    /// <summary>Точка в пикселях экрана (<c>POINT</c>).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        /// <summary>X.</summary>
        public int X;

        /// <summary>Y.</summary>
        public int Y;
    }

    /// <summary>Описание класса окна (<c>WNDCLASSEXW</c>).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct WndClassEx
    {
        /// <summary>Размер структуры в байтах.</summary>
        public uint Size;

        /// <summary>Стили класса.</summary>
        public uint Style;

        /// <summary>Оконная процедура.</summary>
        public delegate* unmanaged<nint, uint, nint, nint, nint> WndProc;

        /// <summary>Дополнительные байты класса.</summary>
        public int ClassExtra;

        /// <summary>Дополнительные байты окна.</summary>
        public int WindowExtra;

        /// <summary>Экземпляр модуля.</summary>
        public nint Instance;

        /// <summary>Иконка.</summary>
        public nint Icon;

        /// <summary>Курсор.</summary>
        public nint Cursor;

        /// <summary>Кисть фона.</summary>
        public nint Background;

        /// <summary>Имя меню.</summary>
        public char* MenuName;

        /// <summary>Имя класса.</summary>
        public char* ClassName;

        /// <summary>Малая иконка.</summary>
        public nint IconSmall;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint window, int id);

    /// <summary>Интервал двойного щелчка из настроек мыши Windows, мс.</summary>
    [LibraryImport("user32.dll")]
    public static partial uint GetDoubleClickTime();

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial ushort RegisterClassExW(WndClassEx* windowClass);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterClassW(string className, nint instance);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowExW(
        uint exStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll")]
    public static partial nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

    /// <summary>Сообщение о смене состояния сеанса (<c>WM_WTSSESSION_CHANGE</c>).</summary>
    public const uint WmWtsSessionChange = 0x02B1;

    /// <summary>К сеансу снова подключена консоль: возврат после переключения пользователей.</summary>
    public const int WtsConsoleConnect = 0x1;

    /// <summary>К сеансу подключился удалённый рабочий стол.</summary>
    public const int WtsRemoteConnect = 0x3;

    /// <summary>Уведомления только о своём сеансе (<c>NOTIFY_FOR_THIS_SESSION</c>).</summary>
    public const uint NotifyForThisSession = 0;

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSRegisterSessionNotification(nint window, uint flags);

    [LibraryImport("wtsapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSUnRegisterSessionNotification(nint window);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out Point point);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandleW(string? moduleName);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(nint window, int attribute, in int value, int size);

    /// <summary>SPI_GETCLIENTAREAANIMATION: включены ли анимации интерфейса («Эффекты анимации» в параметрах).</summary>
    public const uint SpiGetClientAreaAnimation = 0x1042;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SystemParametersInfoW(uint action, uint param, [MarshalAs(UnmanagedType.Bool)] out bool value, uint winIni);

    /// <summary>
    /// Системная настройка «Эффекты анимации» Windows; если её не удалось прочитать — анимации считаются включёнными.
    /// </summary>
    public static bool AnimationsEnabled()
        => !SystemParametersInfoW(SpiGetClientAreaAnimation, 0, out var enabled, 0) || enabled;
}

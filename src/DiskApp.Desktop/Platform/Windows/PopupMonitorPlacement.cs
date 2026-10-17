using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Avalonia;

using Serilog;

namespace DiskApp.Desktop.Platform.Windows;

/// <summary>
/// Обход ошибки Avalonia 12.1.4 на мониторах с разным масштабом. Всплывающее окно Avalonia (подсказка, меню, список)
/// создаётся в точке (0, 0) — на основном мониторе — и берёт его масштаб, а потом переносится к месту показа без
/// пересчёта масштаба (<c>PopupImpl.MoveResize</c>: «We ignore the scaling override for now»). На мониторе 100 %
/// рядом с основным 125 % подсказка получает 80 % нужного места и показывает только первую строку: «Добавить рабочую»
/// вместо «Добавить рабочую папку», «D:» вместо пути (FEEDBACK.md, 2026-10-12 и 2026-10-17).
/// Хук <c>WH_CBT</c> потока интерфейса сдвигает такое окно в момент создания на монитор, где его покажут:
/// под курсором, если курсор над окном-владельцем (подсказка, контекстное меню, щелчок по списку), иначе — на монитор
/// владельца. Масштаб при создании тогда совпадает с масштабом места показа.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class PopupMonitorPlacement
{
    private const string AvaloniaClassPrefix = "Avalonia-";

    private static nint _hook;
    private static string _classPrefix = AvaloniaClassPrefix;

    /// <summary>
    /// Ставит хук на текущий поток; вызывать на UI-потоке Avalonia после её инициализации. Повторный вызов ничего не делает.
    /// </summary>
    /// <param name="classPrefix">Начало имени класса всплывающих окон; другое — только в тестах.</param>
    /// <returns>Снимает хук при освобождении.</returns>
    /// <exception cref="Win32Exception">Windows отказала в установке хука.</exception>
    public static IDisposable Install(string classPrefix = AvaloniaClassPrefix)
    {
        if (_hook == 0)
        {
            _classPrefix = classPrefix;
            _hook = NativeMethods.SetWindowsHookExW(NativeMethods.WhCbt, &HookProcedure, 0, NativeMethods.GetCurrentThreadId());
            if (_hook == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Не удалось поставить хук создания окон.");
            }
        }

        return new Uninstaller();
    }

    /// <summary>
    /// Всплывающее окно Avalonia: <c>PopupImpl</c> создаёт его со стилем <c>WS_POPUP</c>, расширенным стилем
    /// <c>WS_EX_TOOLWINDOW</c> и владельцем — окном, в котором открыт элемент.
    /// </summary>
    public static bool IsAvaloniaPopup(uint style, uint exStyle, nint owner, string className, string classPrefix = AvaloniaClassPrefix)
        => (style & NativeMethods.WsPopup) != 0
           && (exStyle & NativeMethods.WsExToolWindow) != 0
           && owner != 0
           && className.StartsWith(classPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Точка, по которой выбирается монитор всплывающего окна: курсор, если он над владельцем, иначе центр владельца.
    /// </summary>
    public static PixelPoint ChooseAnchor(PixelRect owner, PixelPoint? cursor)
        => cursor is { } point && owner.Contains(point) ? point : owner.Center;

    [UnmanagedCallersOnly]
    private static nint HookProcedure(int code, nint wParam, nint lParam)
    {
        if (code == NativeMethods.HcbtCreateWnd)
        {
            try
            {
                PlaceOnTargetMonitor(wParam, ((NativeMethods.CbtCreateWnd*)lParam)->CreateStruct);
            }
            catch (Exception exception)
            {
                // Исключение нельзя выпускать в неуправляемый код: процесс упадёт без журнала.
                Log.Error(exception, "Не удалось выбрать монитор для всплывающего окна");
            }
        }

        return NativeMethods.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static void PlaceOnTargetMonitor(nint window, NativeMethods.CreateStruct* create)
    {
        if (!IsAvaloniaPopup(create->Style, create->ExStyle, create->Parent, NativeMethods.GetClassName(window), _classPrefix)
            || !NativeMethods.GetWindowRect(create->Parent, out var ownerRect)
            || ownerRect.Right <= ownerRect.Left)
        {
            return;
        }

        var owner = new PixelRect(ownerRect.Left, ownerRect.Top, ownerRect.Right - ownerRect.Left, ownerRect.Bottom - ownerRect.Top);
        var anchor = ChooseAnchor(owner, WindowEffects.GetCursorPosition());
        var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.Point { X = anchor.X, Y = anchor.Y }, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfo { Size = (uint)sizeof(NativeMethods.MonitorInfo) };
        if (monitor == 0 || !NativeMethods.GetMonitorInfoW(monitor, ref info))
        {
            return;
        }

        // Левый верхний угол рабочей области: окно ещё скрыто, Avalonia сама поставит его к месту показа.
        create->X = info.Work.Left;
        create->Y = info.Work.Top;
    }

    private sealed class Uninstaller : IDisposable
    {
        public void Dispose()
        {
            if (_hook != 0)
            {
                NativeMethods.UnhookWindowsHookEx(_hook);
                _hook = 0;
                _classPrefix = AvaloniaClassPrefix;
            }
        }
    }
}

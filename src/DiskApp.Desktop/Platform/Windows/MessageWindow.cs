using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Serilog;

namespace DiskApp.Desktop.Platform.Windows;

/// <summary>
/// Невидимое окно, которое только принимает сообщения Windows (<c>HWND_MESSAGE</c>). Нужно для
/// <c>RegisterHotKey</c>: <c>WM_HOTKEY</c> приходит в его оконную процедуру через цикл сообщений Avalonia.
/// Создавать на UI-потоке: сообщения доставляются потоку, создавшему окно.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class MessageWindow : IDisposable
{
    private const string ClassName = "DiskApp.MessageWindow";

    private static readonly ConcurrentDictionary<nint, MessageWindow> Windows = new();
    private static readonly object RegistrationGate = new();
    private static ushort _classAtom;

    private nint _handle;

    /// <summary>
    /// Регистрирует класс окна (один раз на процесс) и создаёт окно сообщений.
    /// </summary>
    /// <exception cref="Win32Exception">Windows отказала в создании окна.</exception>
    public MessageWindow()
    {
        var instance = NativeMethods.GetModuleHandleW(null);
        EnsureClassRegistered(instance);

        _handle = NativeMethods.CreateWindowExW(0, ClassName, "DiskApp", 0, 0, 0, 0, 0, NativeMethods.HwndMessage, 0, instance, 0);
        if (_handle == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Не удалось создать служебное окно сообщений.");
        }

        Windows[_handle] = this;
    }

    /// <summary>Нажата горячая клавиша; аргумент — идентификатор из <c>RegisterHotKey</c>.</summary>
    public event EventHandler<int>? HotkeyPressed;

    /// <summary>Дескриптор окна.</summary>
    public nint Handle => _handle;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_handle == 0)
        {
            return;
        }

        Windows.TryRemove(_handle, out _);
        NativeMethods.DestroyWindow(_handle);
        _handle = 0;
    }

    private static void EnsureClassRegistered(nint instance)
    {
        lock (RegistrationGate)
        {
            if (_classAtom != 0)
            {
                return;
            }

            fixed (char* className = ClassName)
            {
                var windowClass = new NativeMethods.WndClassEx
                {
                    Size = (uint)sizeof(NativeMethods.WndClassEx),
                    WndProc = &WindowProcedure,
                    Instance = instance,
                    ClassName = className,
                };

                var atom = NativeMethods.RegisterClassExW(&windowClass);
                if (atom == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Не удалось зарегистрировать класс служебного окна.");
                }

                _classAtom = atom;
            }
        }
    }

    [UnmanagedCallersOnly]
    private static nint WindowProcedure(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == NativeMethods.WmHotkey && Windows.TryGetValue(window, out var owner))
        {
            try
            {
                owner.HotkeyPressed?.Invoke(owner, (int)wParam);
            }
            catch (Exception exception)
            {
                // Исключение нельзя выпускать в неуправляемый код: процесс упадёт без журнала.
                Log.Error(exception, "Ошибка обработчика горячей клавиши {Id}", (int)wParam);
            }

            return 0;
        }

        return NativeMethods.DefWindowProcW(window, message, wParam, lParam);
    }
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DiskApp.Desktop.Platform.Windows;

/// <summary>
/// Глобальная горячая клавиша Windows через <c>RegisterHotKey</c> (решение 7 плана: без клавиатурных хуков).
/// Нажатие приходит в <see cref="MessageWindow"/> и поднимается событием <see cref="Pressed"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class GlobalHotkey : IDisposable
{
    private readonly MessageWindow _window;
    private readonly int _id;
    private bool _registered;

    private GlobalHotkey(MessageWindow window, int id)
    {
        _window = window;
        _id = id;
        _registered = true;
        _window.HotkeyPressed += OnHotkeyPressed;
    }

    /// <summary>Горячая клавиша нажата.</summary>
    public event EventHandler? Pressed;

    /// <summary>
    /// Регистрирует сочетание. Повтор при удержании отключён (<c>MOD_NOREPEAT</c>).
    /// </summary>
    /// <param name="window">Окно сообщений, созданное на UI-потоке.</param>
    /// <param name="id">Идентификатор сочетания, уникальный в пределах окна.</param>
    /// <param name="modifiers">Комбинация <c>NativeMethods.Mod*</c>.</param>
    /// <param name="virtualKey">Виртуальный код клавиши.</param>
    /// <exception cref="Win32Exception">Сочетание занято другим приложением или недопустимо.</exception>
    public static GlobalHotkey Register(MessageWindow window, int id, uint modifiers, uint virtualKey)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!NativeMethods.RegisterHotKey(window.Handle, id, modifiers | NativeMethods.ModNoRepeat, virtualKey))
        {
            var error = Marshal.GetLastPInvokeError();
            var reason = error == NativeMethods.ErrorHotkeyAlreadyRegistered
                ? "сочетание уже занято другим приложением"
                : "Windows отклонила регистрацию";
            throw new Win32Exception(error, $"Не удалось зарегистрировать горячую клавишу: {reason}.");
        }

        return new GlobalHotkey(window, id);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_registered)
        {
            return;
        }

        _window.HotkeyPressed -= OnHotkeyPressed;
        NativeMethods.UnregisterHotKey(_window.Handle, _id);
        _registered = false;
    }

    private void OnHotkeyPressed(object? sender, int id)
    {
        if (id == _id)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
        }
    }
}

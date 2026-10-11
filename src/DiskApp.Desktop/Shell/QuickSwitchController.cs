using System.ComponentModel;
using System.Runtime.Versioning;

using DiskApp.Core.Abstractions;
using DiskApp.Core.Settings;
using DiskApp.Desktop.Platform.Windows;
using DiskApp.UI.Services;

using Serilog;

namespace DiskApp.Desktop.Shell;

/// <summary>
/// Quick Switch (PLAN.md, этап 8.6, ADR-0008): флаг <c>experimental.quickSwitch</c> и вторая глобальная горячая клавиша
/// <c>hotkeys.quickSwitch</c>. Сочетание регистрируется, только пока функция включена, — выключенная функция не
/// занимает клавиши у других программ.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class QuickSwitchController : IQuickSwitchSettings, IDisposable
{
    private const int HotkeyId = 2;

    private readonly ISettingsStore<AppSettings> _settings;
    private readonly ILogger _log;
    private MessageWindow? _window;
    private Action? _pressed;
    private GlobalHotkey? _hotkey;

    /// <summary>
    /// Создаёт контроллер; горячая клавиша появляется после <see cref="Attach"/>.
    /// </summary>
    public QuickSwitchController(ISettingsStore<AppSettings> settings, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);

        _settings = settings;
        _log = log.ForContext<QuickSwitchController>();
    }

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public bool IsEnabled => _settings.Current.Experimental.QuickSwitch;

    /// <inheritdoc />
    public string HotkeyText => Configured().DisplayText;

    /// <summary>Сочетание сейчас зарегистрировано.</summary>
    public bool IsRegistered => _hotkey is not null;

    /// <summary>
    /// Подключает окно сообщений и обработчик нажатия; если функция включена — регистрирует сочетание.
    /// Ошибка регистрации при запуске только пишется в журнал: функция экспериментальная и не должна мешать старту.
    /// </summary>
    public void Attach(MessageWindow window, Action pressed)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(pressed);

        _window = window;
        _pressed = pressed;
        if (!IsEnabled)
        {
            return;
        }

        try
        {
            Register();
        }
        catch (InvalidOperationException exception)
        {
            _log.Warning("Quick Switch включён, но сочетание не зарегистрировано: {Reason}", exception.Message);
        }
    }

    /// <inheritdoc />
    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            Register();
        }
        else
        {
            Unregister();
        }

        var current = _settings.Current;
        if (current.Experimental.QuickSwitch != enabled)
        {
            _ = SaveAsync(current with { Experimental = current.Experimental with { QuickSwitch = enabled } });
        }
    }

    /// <inheritdoc />
    public void Dispose() => Unregister();

    private Hotkey Configured()
        => Hotkey.TryParse(_settings.Current.Hotkeys.QuickSwitch, out var hotkey) ? hotkey : Hotkey.DefaultQuickSwitch;

    /// <summary>
    /// Регистрирует сочетание; запасных нет — Quick Switch нужен редко, и чужое сочетание лучше не угадывать.
    /// </summary>
    /// <exception cref="InvalidOperationException">Сочетание занято или клавиша не поддерживается.</exception>
    private void Register()
    {
        if (_hotkey is not null || _window is null)
        {
            return;
        }

        var hotkey = Configured();
        if (HotkeyKeys.ToVirtualKey(hotkey.Key) is not { } virtualKey)
        {
            throw new InvalidOperationException($"Клавиша {hotkey.DisplayText} не поддерживается для Quick Switch.");
        }

        try
        {
            _hotkey = GlobalHotkey.Register(_window, HotkeyId, (uint)hotkey.Modifiers, virtualKey);
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException($"Quick Switch не включён: {hotkey.DisplayText} — {exception.Message}", exception);
        }

        _hotkey.Pressed += (_, _) => _pressed?.Invoke();
        _log.Information("Quick Switch: сочетание {Hotkey} зарегистрировано", hotkey.DisplayText);
    }

    private void Unregister()
    {
        _hotkey?.Dispose();
        _hotkey = null;
    }

    private async Task SaveAsync(AppSettings updated)
    {
        try
        {
            await _settings.SaveAsync(updated, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Warning(exception, "Настройка Quick Switch не сохранилась");
        }
    }
}

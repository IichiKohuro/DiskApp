namespace DiskApp.UI.Services;

/// <summary>
/// Quick Switch (PLAN.md, этап 8.6, ADR-0008): экспериментальная функция, сочетание клавиш в стандартном диалоге
/// «Сохранить» или «Открыть» переводит диалог в папку, выбранную в быстром поиске.
/// </summary>
public interface IQuickSwitchSettings
{
    /// <summary>Функция поддерживается на этой платформе.</summary>
    bool IsSupported { get; }

    /// <summary>Функция включена.</summary>
    bool IsEnabled { get; }

    /// <summary>Сочетание клавиш для подписи: «Ctrl + Alt + G».</summary>
    string HotkeyText { get; }

    /// <summary>
    /// Включает или выключает функцию и сохраняет выбор.
    /// </summary>
    /// <exception cref="InvalidOperationException">Сочетание не удалось зарегистрировать; функция осталась выключенной.</exception>
    void SetEnabled(bool enabled);
}

/// <summary>
/// Quick Switch недоступен (не Windows, превью, тесты).
/// </summary>
public sealed class NullQuickSwitchSettings : IQuickSwitchSettings
{
    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public bool IsEnabled => false;

    /// <inheritdoc />
    public string HotkeyText => Hotkey.DefaultQuickSwitch.DisplayText;

    /// <inheritdoc />
    public void SetEnabled(bool enabled)
    {
    }
}

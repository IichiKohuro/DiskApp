using CommunityToolkit.Mvvm.ComponentModel;

namespace DiskApp.UI.Services;

/// <summary>
/// Какие глобальные сочетания действуют сейчас: быстрый поиск, главное окно и окно Drag&amp;Drop. Оболочка заполняет
/// состояние после регистрации (с учётом запасных вариантов, ADR-0006 и ADR-0011), интерфейс показывает его в настройках
/// и подсказках.
/// </summary>
public sealed partial class HotkeyStatus : ObservableObject
{
    /// <summary>Сочетание по умолчанию в подписи для человека.</summary>
    public const string DefaultQuickSearchText = "Ctrl + Alt + D";

    /// <summary>
    /// Создаёт состояние с сочетаниями по умолчанию, ещё не зарегистрированными.
    /// </summary>
    public HotkeyStatus()
    {
        QuickSearchText = DefaultQuickSearchText;
        Message = string.Empty;
        MainWindowText = Hotkey.DefaultMainWindow.DisplayText;
        MainWindowMessage = string.Empty;
        DropWindowText = Hotkey.DefaultDropWindow.DisplayText;
        DropWindowMessage = string.Empty;
    }

    /// <summary>Подпись действующего сочетания для быстрого поиска, например «Ctrl + Alt + D».</summary>
    [ObservableProperty]
    public partial string QuickSearchText { get; set; }

    /// <summary>Сочетание быстрого поиска зарегистрировано в системе.</summary>
    [ObservableProperty]
    public partial bool IsRegistered { get; set; }

    /// <summary>Пояснение к быстрому поиску, если действует запасное сочетание или регистрация не удалась; пусто, если всё в порядке.</summary>
    [ObservableProperty]
    public partial string Message { get; set; }

    /// <summary>Подпись действующего сочетания главного окна; пусто, если оно не зарегистрировано.</summary>
    [ObservableProperty]
    public partial string MainWindowText { get; set; }

    /// <summary>Пояснение к сочетанию главного окна; пусто, если всё в порядке.</summary>
    [ObservableProperty]
    public partial string MainWindowMessage { get; set; }

    /// <summary>Подпись действующего сочетания окна Drag&amp;Drop; пусто, если оно не зарегистрировано.</summary>
    [ObservableProperty]
    public partial string DropWindowText { get; set; }

    /// <summary>Пояснение к сочетанию окна Drag&amp;Drop; пусто, если всё в порядке.</summary>
    [ObservableProperty]
    public partial string DropWindowMessage { get; set; }

    /// <summary>
    /// Записывает итог регистрации сочетания функции.
    /// </summary>
    /// <param name="target">Функция.</param>
    /// <param name="text">Действующее сочетание в подписи для человека; пусто, если не зарегистрировано.</param>
    /// <param name="message">Пояснение; пусто, если действует выбранное сочетание.</param>
    public void Set(HotkeyTarget target, string text, string message)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(message);

        switch (target)
        {
            case HotkeyTarget.QuickSearch:
                IsRegistered = text.Length > 0;
                if (text.Length > 0)
                {
                    QuickSearchText = text;
                }

                Message = message;
                break;
            case HotkeyTarget.MainWindow:
                MainWindowText = text;
                MainWindowMessage = message;
                break;
            case HotkeyTarget.DropWindow:
                DropWindowText = text;
                DropWindowMessage = message;
                break;
        }
    }
}

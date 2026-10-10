using CommunityToolkit.Mvvm.ComponentModel;

namespace DiskApp.UI.Services;

/// <summary>
/// Какое сочетание клавиш для быстрого поиска действует сейчас. Оболочка заполняет его после регистрации
/// (с учётом запасных вариантов, ADR-0006), интерфейс показывает в настройках и подсказках.
/// </summary>
public sealed partial class HotkeyStatus : ObservableObject
{
    /// <summary>Сочетание по умолчанию в подписи для человека.</summary>
    public const string DefaultQuickSearchText = "Ctrl + Alt + D";

    /// <summary>
    /// Создаёт состояние с сочетанием по умолчанию, ещё не зарегистрированным.
    /// </summary>
    public HotkeyStatus()
    {
        QuickSearchText = DefaultQuickSearchText;
        Message = string.Empty;
    }

    /// <summary>Подпись действующего сочетания для быстрого поиска, например «Ctrl + Alt + D».</summary>
    [ObservableProperty]
    public partial string QuickSearchText { get; set; }

    /// <summary>Сочетание зарегистрировано в системе.</summary>
    [ObservableProperty]
    public partial bool IsRegistered { get; set; }

    /// <summary>Пояснение, если действует запасное сочетание или регистрация не удалась; пусто, если всё в порядке.</summary>
    [ObservableProperty]
    public partial string Message { get; set; }
}

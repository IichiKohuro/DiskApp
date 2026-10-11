using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DiskApp.UI.Localization;
using DiskApp.UI.Services;

namespace DiskApp.UI.ViewModels;

/// <summary>
/// Окно Drag&amp;Drop у значка в трее (PLAN.md, этап 18, ADR-0011): открывается щелчком по значку или своим хоткеем.
/// При первом показе после обновления подсказывает, где теперь главное окно и быстрый поиск: раньше щелчок по значку
/// открывал поиск. Приём файлов в область «Бросить в накопитель» — этап 18.2.
/// </summary>
public sealed partial class DropWindowViewModel : ObservableObject
{
    private readonly HotkeyStatus _hotkeys;
    private readonly ITipsService _tips;

    /// <summary>
    /// Создаёт модель окна.
    /// </summary>
    public DropWindowViewModel(HotkeyStatus hotkeys, ITipsService tips)
    {
        ArgumentNullException.ThrowIfNull(hotkeys);
        ArgumentNullException.ThrowIfNull(tips);

        _hotkeys = hotkeys;
        _tips = tips;
    }

    /// <summary>Окно просит закрыть его: кнопка или <c>Esc</c>.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Подсказка под областью; пусто — не показывается.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHint))]
    public partial string Hint { get; set; } = string.Empty;

    /// <summary>Подсказка показана.</summary>
    public bool HasHint => Hint.Length > 0;

    /// <summary>
    /// Окно показано: при первом показе — подсказка о новом поведении значка.
    /// </summary>
    public void OnShown()
    {
        if (!HasHint && _tips.ShouldShow(Tips.TrayClicks))
        {
            // Неразрывные пробелы: сочетание не разрывается переносом строки («Ctrl / + Alt + F»).
            Hint = _hotkeys.IsRegistered
                ? L.F("Drop.TrayHint", _hotkeys.QuickSearchText.Replace(' ', ' '))
                : L.T("Drop.TrayHintNoHotkey");
        }
    }

    /// <summary>
    /// Окно спрятано: показанная подсказка больше не появится.
    /// </summary>
    public void OnHidden()
    {
        if (!HasHint)
        {
            return;
        }

        _tips.MarkShown(Tips.TrayClicks);
        Hint = string.Empty;
    }

    /// <summary>Закрыть окно.</summary>
    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}

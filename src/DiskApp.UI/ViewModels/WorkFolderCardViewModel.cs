using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DiskApp.UI.Localization;
using DiskApp.UI.Services;
using DiskApp.UI.Text;

namespace DiskApp.UI.ViewModels;

/// <summary>Чем закончилась карточка рабочей папки.</summary>
public enum WorkFolderCardResult
{
    /// <summary>Закрыта без изменений.</summary>
    Cancelled,

    /// <summary>Изменения сохранить.</summary>
    Saved,

    /// <summary>Убрать папку из рабочих (файлы остаются).</summary>
    Removed,
}

/// <summary>
/// Карточка рабочей папки (PLAN.md, этап 17.2): подпись, номер — он же клавиша 1–9 на странице «Разбор», — и сама папка.
/// Папку, переименованную или перенесённую так, что DiskApp её потерял, здесь можно выбрать заново. Здесь же авто-теги
/// папки (этап 19); шаблоны имён (17.5) появятся в этой же карточке.
/// </summary>
public sealed partial class WorkFolderCardViewModel : ObservableObject
{
    private readonly IDialogService? _dialogs;

    /// <summary>
    /// Создаёт карточку.
    /// </summary>
    /// <param name="folder">Рабочая папка.</param>
    /// <param name="position">Её место в списке, с нуля.</param>
    /// <param name="count">Сколько всего рабочих папок.</param>
    /// <param name="isMissing">Папка не найдена в индексе.</param>
    /// <param name="dialogs">Диалоги: выбор другой папки.</param>
    /// <param name="autoTags">Поле авто-тегов; <c>null</c> — их не удалось прочитать, и поле не показывается.</param>
    public WorkFolderCardViewModel(SortingFolderInfo folder, int position, int count, bool isMissing, IDialogService? dialogs = null, AutoTagsEditorViewModel? autoTags = null)
    {
        ArgumentNullException.ThrowIfNull(folder);

        _dialogs = dialogs;
        AutoTags = autoTags;
        Path = folder.Path;
        Label = folder.Label ?? string.Empty;
        IsMissing = isMissing;
        Positions = [.. Enumerable.Range(0, Math.Max(count, 1)).Select(static index => new SettingOption<int>(index, index < 9
            ? L.F("Sorting.CardPositionKey", index + 1)
            : L.F("Sorting.CardPositionNoKey", index + 1)))];
        SelectedPosition = Positions[Math.Clamp(position, 0, Positions.Count - 1)];
    }

    /// <summary>Полный путь папки.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderName), nameof(Title))]
    public partial string Path { get; set; }

    /// <summary>Подпись; пусто — имя папки.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string Label { get; set; }

    /// <summary>Папка не найдена: переименована, перенесена или удалена.</summary>
    [ObservableProperty]
    public partial bool IsMissing { get; set; }

    /// <summary>Авто-теги папки; <c>null</c> — поле скрыто.</summary>
    public AutoTagsEditorViewModel? AutoTags { get; }

    /// <summary>Поле авто-тегов показывается.</summary>
    public bool HasAutoTags => AutoTags is not null;

    /// <summary>Места в списке: первые девять — с клавишей.</summary>
    public IReadOnlyList<SettingOption<int>> Positions { get; }

    /// <summary>Выбранное место.</summary>
    [ObservableProperty]
    public partial SettingOption<int> SelectedPosition { get; set; }

    /// <summary>Имя папки — подпись по умолчанию.</summary>
    public string FolderName => PathText.FileName(Path);

    /// <summary>Заголовок: подпись или имя папки.</summary>
    public string Title => string.IsNullOrWhiteSpace(Label) ? FolderName : Label.Trim();

    /// <summary>Рабочая папка с изменениями карточки.</summary>
    public SortingFolderInfo ToFolder() => new(Path, string.IsNullOrWhiteSpace(Label) ? null : Label.Trim());

    /// <summary>Выбрать другую папку: прежняя переименована или перенесена.</summary>
    [RelayCommand]
    private async Task PickFolderAsync()
    {
        if (_dialogs is not null && await _dialogs.PickFolderAsync(L.T("Sorting.PickWorkFolder")) is { Length: > 0 } picked)
        {
            Path = picked;
            IsMissing = false;
        }
    }
}

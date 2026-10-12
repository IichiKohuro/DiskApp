using System.Collections.ObjectModel;

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
/// папки (этап 19) и шаблоны имён (этап 17.5) с сухим прогоном: что из накопителей подходит к ним сейчас.
/// </summary>
public sealed partial class WorkFolderCardViewModel : ObservableObject
{
    private readonly IDialogService? _dialogs;
    private readonly IReadOnlyList<SortingFolderInfo> _inboxes;
    private readonly Func<SortingFolderInfo, CancellationToken, Task<string>>? _preview;
    private CancellationTokenSource? _pendingPreview;

    /// <summary>
    /// Создаёт карточку.
    /// </summary>
    /// <param name="folder">Рабочая папка.</param>
    /// <param name="position">Её место в списке, с нуля.</param>
    /// <param name="count">Сколько всего рабочих папок.</param>
    /// <param name="isMissing">Папка не найдена в индексе.</param>
    /// <param name="dialogs">Диалоги: выбор другой папки.</param>
    /// <param name="autoTags">Поле авто-тегов; <c>null</c> — их не удалось прочитать, и поле не показывается.</param>
    /// <param name="inboxes">Накопители — для условия шаблона «только из накопителя».</param>
    /// <param name="preview">Сухой прогон шаблонов: текст о подходящих файлах; <c>null</c> — без него.</param>
    public WorkFolderCardViewModel(
        SortingFolderInfo folder,
        int position,
        int count,
        bool isMissing,
        IDialogService? dialogs = null,
        AutoTagsEditorViewModel? autoTags = null,
        IReadOnlyList<SortingFolderInfo>? inboxes = null,
        Func<SortingFolderInfo, CancellationToken, Task<string>>? preview = null)
    {
        ArgumentNullException.ThrowIfNull(folder);

        _dialogs = dialogs;
        _inboxes = inboxes ?? [];
        _preview = preview;
        foreach (var rule in folder.Rules)
        {
            Rules.Add(CreateRule(rule));
        }

        Rules.CollectionChanged += (_, _) => OnRulesChanged();
        AutoTags = autoTags;
        Path = folder.Path;
        Label = folder.Label ?? string.Empty;
        IsMissing = isMissing;
        Positions = [.. Enumerable.Range(0, Math.Max(count, 1)).Select(static index => new SettingOption<int>(index, index < 9
            ? L.F("Sorting.CardPositionKey", index + 1)
            : L.F("Sorting.CardPositionNoKey", index + 1)))];
        SelectedPosition = Positions[Math.Clamp(position, 0, Positions.Count - 1)];
        PendingPreview = UpdatePreviewAsync(TimeSpan.Zero);
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

    /// <summary>Шаблоны имён (этап 17.5).</summary>
    public ObservableCollection<SortingRuleEditorViewModel> Rules { get; } = [];

    /// <summary>Шаблонов ещё нет: показывается подсказка, зачем они.</summary>
    public bool HasNoRules => Rules.Count == 0;

    /// <summary>Сухой прогон: сколько файлов накопителей подходит к шаблонам сейчас.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    public partial string PreviewText { get; set; } = string.Empty;

    /// <summary>Сухой прогон показан.</summary>
    public bool HasPreview => PreviewText.Length > 0;

    /// <summary>Карточку можно сохранить: в шаблонах нет ошибок.</summary>
    public bool CanSave => !Rules.Any(static rule => rule.HasProblem);

    /// <summary>Завершается, когда обновлён сухой прогон (для тестов).</summary>
    public Task PendingPreview { get; private set; } = Task.CompletedTask;

    /// <summary>Рабочая папка с изменениями карточки; шаблоны с пустой маской не сохраняются.</summary>
    public SortingFolderInfo ToFolder() => new(Path, string.IsNullOrWhiteSpace(Label) ? null : Label.Trim())
    {
        Rules = [.. Rules.Select(static rule => rule.ToRule()).OfType<SortingRuleInfo>()],
    };

    /// <summary>Добавляет шаблон: новый — «переносить сам».</summary>
    [RelayCommand]
    private void AddRule() => Rules.Add(CreateRule(new SortingRuleInfo(string.Empty)));

    partial void OnPathChanged(string value) => PendingPreview = UpdatePreviewAsync(TimeSpan.FromMilliseconds(300));

    private SortingRuleEditorViewModel CreateRule(SortingRuleInfo rule)
    {
        var editor = new SortingRuleEditorViewModel(rule, _inboxes, remove: item => Rules.Remove(item));
        editor.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(CanSave));
            PendingPreview = UpdatePreviewAsync(TimeSpan.FromMilliseconds(300));
        };
        return editor;
    }

    private void OnRulesChanged()
    {
        OnPropertyChanged(nameof(HasNoRules));
        OnPropertyChanged(nameof(CanSave));
        PendingPreview = UpdatePreviewAsync(TimeSpan.Zero);
    }

    // Сухой прогон — после паузы в вводе; без шаблонов его нет.
    private async Task UpdatePreviewAsync(TimeSpan delay)
    {
        if (_preview is null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _pendingPreview, cts)?.Cancel();
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cts.Token);
            }

            var folder = ToFolder();
            PreviewText = folder.Rules.Count == 0 || !CanSave ? string.Empty : await _preview(folder, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Пользователь продолжил ввод.
        }
        catch (Exception exception)
        {
            // Без сервера сухого прогона нет, но шаблоны всё равно сохраняются.
            _ = exception;
            PreviewText = string.Empty;
        }
    }

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

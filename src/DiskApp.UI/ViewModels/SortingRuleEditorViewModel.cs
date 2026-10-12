using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DiskApp.UI.Localization;
using DiskApp.UI.Services;
using DiskApp.UI.Text;

namespace DiskApp.UI.ViewModels;

/// <summary>
/// Шаблон имени рабочей папки в редакторе (PLAN.md, этап 17.5): маска, «переносить сам» или «предлагать первой», флажок
/// «действует» и редкие условия — накопитель, расширения, подпапка — за кнопкой «Условия».
/// </summary>
public sealed partial class SortingRuleEditorViewModel : ObservableObject
{
    private readonly IReadOnlyList<SortingFolderInfo> _inboxes;
    private readonly Action<SortingRuleEditorViewModel>? _remove;

    /// <summary>
    /// Создаёт редактор.
    /// </summary>
    /// <param name="rule">Шаблон.</param>
    /// <param name="inboxes">Накопители — для условия «только из накопителя».</param>
    /// <param name="remove">Убрать шаблон из списка; <c>null</c> — кнопки нет.</param>
    public SortingRuleEditorViewModel(SortingRuleInfo rule, IReadOnlyList<SortingFolderInfo> inboxes, Action<SortingRuleEditorViewModel>? remove = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _inboxes = inboxes ?? [];
        _remove = remove;
        Modes = [new SettingOption<bool>(true, "Sorting.RuleAuto"), new SettingOption<bool>(false, "Sorting.RuleSuggest")];
        InboxOptions = [new SettingOption<string?>(null, "Sorting.RuleAnyInbox"), .. _inboxes.Select(static inbox => new SettingOption<string?>(inbox.Path, inbox.Title))];
        Pattern = rule.Pattern;
        Enabled = rule.Enabled;
        SelectedMode = Modes[rule.Auto ? 0 : 1];
        SelectedInbox = InboxOptions.FirstOrDefault(option => option.Value is not null && string.Equals(option.Value, rule.Inbox, StringComparison.OrdinalIgnoreCase))
            ?? (rule.Inbox is null ? InboxOptions[0] : new SettingOption<string?>(rule.Inbox, PathText.FileName(rule.Inbox)));
        ExtensionsText = string.Join(", ", rule.Extensions);
        Subfolder = rule.Subfolder ?? string.Empty;
    }

    /// <summary>Что-то поменялось: пора обновить сухой прогон.</summary>
    public event EventHandler? Changed;

    /// <summary>Маска имени: <c>417_*</c>, <c>*TCI2*</c>; без <c>*</c> — где угодно в имени.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem), nameof(HasProblem))]
    public partial string Pattern { get; set; }

    /// <summary>Шаблон действует.</summary>
    [ObservableProperty]
    public partial bool Enabled { get; set; }

    /// <summary>«Переносить сам» или «предлагать первой».</summary>
    public IReadOnlyList<SettingOption<bool>> Modes { get; }

    /// <summary>Выбранный режим.</summary>
    [ObservableProperty]
    public partial SettingOption<bool> SelectedMode { get; set; }

    /// <summary>«Из любого накопителя» и накопители.</summary>
    public IReadOnlyList<SettingOption<string?>> InboxOptions { get; }

    /// <summary>Условие «только из накопителя».</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConditionsText), nameof(HasConditions))]
    public partial SettingOption<string?> SelectedInbox { get; set; }

    /// <summary>Расширения через запятую или пробел: <c>pdf, docx</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConditionsText), nameof(HasConditions))]
    public partial string ExtensionsText { get; set; }

    /// <summary>Подпапка рабочей папки: <c>Переписка</c>, <c>2026\Акты</c>; пусто — в саму папку.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConditionsText), nameof(HasConditions), nameof(Problem), nameof(HasProblem))]
    public partial string Subfolder { get; set; }

    /// <summary>Есть кнопка «Убрать».</summary>
    public bool CanRemove => _remove is not null;

    /// <summary>Что не так с шаблоном; <c>null</c> — всё в порядке (пустая маска — не ошибка: такой шаблон не сохраняется).</summary>
    public string? Problem
    {
        get
        {
            if (Pattern.IndexOfAny(['\\', '/']) >= 0 || Pattern.Trim().Length > 100)
            {
                return L.T("Sorting.RulePatternProblem");
            }

            var subfolder = Subfolder.Replace('/', '\\').Trim().Trim('\\');
            return subfolder.Length > 0 && (subfolder.Contains(':', StringComparison.Ordinal) || subfolder.Split('\\').Any(static part => part.Trim() is "" or "." or ".."))
                ? L.T("Sorting.RuleSubfolderProblem")
                : null;
        }
    }

    /// <summary>Есть что исправить.</summary>
    public bool HasProblem => Problem is not null;

    /// <summary>Условия одной строкой: «только «Загрузки» · pdf, docx · в «Акты»».</summary>
    public string ConditionsText
    {
        get
        {
            var parts = new List<string>();
            if (SelectedInbox.Value is not null)
            {
                parts.Add(L.F("Sorting.RuleOnlyFrom", SelectedInbox.Text));
            }

            if (Extensions().Count > 0)
            {
                parts.Add(string.Join(", ", Extensions()));
            }

            if (Subfolder.Trim().Trim('\\', '/').Length > 0)
            {
                parts.Add(L.F("Sorting.RuleInto", Subfolder.Trim().Trim('\\', '/')));
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Условия заданы.</summary>
    public bool HasConditions => ConditionsText.Length > 0;

    /// <summary>Шаблон с изменениями; <c>null</c> — маска пустая, такого шаблона нет.</summary>
    public SortingRuleInfo? ToRule()
    {
        var pattern = Pattern.Trim();
        if (pattern.Length == 0)
        {
            return null;
        }

        var subfolder = Subfolder.Replace('/', '\\').Trim().Trim('\\').Trim();
        return new SortingRuleInfo(pattern, SelectedMode.Value, Enabled, SelectedInbox.Value, subfolder.Length == 0 ? null : subfolder) { Extensions = Extensions() };
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Pattern) or nameof(Enabled) or nameof(SelectedMode) or nameof(SelectedInbox) or nameof(ExtensionsText) or nameof(Subfolder))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private List<string> Extensions()
        => [.. ExtensionsText.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static extension => extension.TrimStart('*').TrimStart('.').ToLowerInvariant())
            .Where(static extension => extension.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Убрать шаблон из списка.</summary>
    [RelayCommand]
    private void Remove() => _remove?.Invoke(this);
}

/// <summary>
/// Итог сухого прогона одной строкой (этап 17.5): сколько файлов накопителей подходит сейчас, сколько уйдёт само и сколько
/// подходит ещё и к другой папке.
/// </summary>
public static class SortingPreviewText
{
    /// <summary>
    /// Описывает подошедшие файлы.
    /// </summary>
    /// <param name="items">Сухой прогон.</param>
    /// <param name="isOurs">Совпадение — с этой папкой или этим шаблоном.</param>
    /// <returns>Текст; пусто — подходящих файлов нет и шаблонов тоже.</returns>
    public static string Describe(IReadOnlyList<SortingPreviewInfo> items, Func<SortingMatchInfo, bool> isOurs)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(isOurs);

        var ours = items.Where(item => item.Matches.Any(isOurs)).ToArray();
        if (ours.Length == 0)
        {
            return L.T("Sorting.PreviewNone");
        }

        var names = string.Join(", ", ours.Take(3).Select(static item => $"«{item.Name}»")) + (ours.Length > 3 ? L.T("Sorting.PreviewMore") : string.Empty);
        var text = L.F("Sorting.PreviewMatching", Formatting.Count(ours.Length, L.T("Common.File.One"), L.T("Common.File.Few"), L.T("Common.File.Many")), names);
        var moving = ours.Count(static item => item.WillMove);
        var choice = ours.Count(static item => item.Hint == SortingHint.Choice);
        if (moving > 0)
        {
            text += " " + L.F("Sorting.PreviewMoving", moving);
        }

        if (choice > 0)
        {
            text += " " + L.F("Sorting.PreviewChoice", choice);
        }

        return text;
    }
}

/// <summary>
/// «Всегда класть такие файлы сюда?» (PLAN.md, этап 17.5): шаблон из решения одним действием — маска по именам выбранных
/// файлов, режим и сухой прогон «сколько подходит сейчас».
/// </summary>
public sealed partial class SortingRuleDialogViewModel : ObservableObject
{
    private readonly Func<SortingRuleInfo, CancellationToken, Task<string>>? _preview;
    private CancellationTokenSource? _pending;

    /// <summary>
    /// Создаёт диалог.
    /// </summary>
    /// <param name="destination">Куда класть: «417 › Акты».</param>
    /// <param name="rule">Шаблон с предложенной маской.</param>
    /// <param name="preview">Сухой прогон: текст о подходящих файлах; <c>null</c> — без него.</param>
    public SortingRuleDialogViewModel(string destination, SortingRuleEditorViewModel rule, Func<SortingRuleInfo, CancellationToken, Task<string>>? preview = null)
    {
        Rule = rule ?? throw new ArgumentNullException(nameof(rule));
        Question = L.F("Sorting.AlwaysHereQuestion", destination);
        _preview = preview;
        Rule.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(CanSave));
            PendingPreview = UpdatePreviewAsync(TimeSpan.FromMilliseconds(300));
        };
        PendingPreview = UpdatePreviewAsync(TimeSpan.Zero);
    }

    /// <summary>Вопрос-заголовок.</summary>
    public string Question { get; }

    /// <summary>Шаблон.</summary>
    public SortingRuleEditorViewModel Rule { get; }

    /// <summary>Сколько файлов подходит сейчас.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    public partial string PreviewText { get; set; } = string.Empty;

    /// <summary>Сухой прогон показан.</summary>
    public bool HasPreview => PreviewText.Length > 0;

    /// <summary>Шаблон можно сохранить: маска задана и без ошибок.</summary>
    public bool CanSave => Rule.ToRule() is not null && !Rule.HasProblem;

    /// <summary>Завершается, когда обновлён сухой прогон (для тестов).</summary>
    public Task PendingPreview { get; private set; } = Task.CompletedTask;

    private async Task UpdatePreviewAsync(TimeSpan delay)
    {
        if (_preview is null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _pending, cts)?.Cancel();
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cts.Token);
            }

            PreviewText = Rule.ToRule() is { } rule && !Rule.HasProblem ? await _preview(rule, cts.Token) : string.Empty;
        }
        catch (OperationCanceledException)
        {
            // Пользователь продолжил ввод.
        }
        catch (Exception exception)
        {
            // Без сервера сухого прогона нет, но шаблон всё равно сохраняется.
            _ = exception;
            PreviewText = string.Empty;
        }
    }
}

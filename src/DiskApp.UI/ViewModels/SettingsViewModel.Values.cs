using System.Text.Json.Nodes;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DiskApp.Contracts.App;
using DiskApp.UI.Input;
using DiskApp.UI.Services;

using RankingPreset = DiskApp.Contracts.Search.RankingPreset;

namespace DiskApp.UI.ViewModels;

/// <summary>Раздел страницы настроек.</summary>
/// <param name="Key">Ключ раздела.</param>
/// <param name="Title">Заголовок.</param>
/// <param name="Icon">Значок.</param>
public sealed record SettingsCategory(string Key, string Title, FluentAvalonia.UI.Controls.FASymbol Icon)
{
    /// <inheritdoc />
    public override string ToString() => Title;
}

/// <summary>Образец акцентного цвета: отмечен, если выбран.</summary>
public sealed partial class AccentChoice(string color) : ObservableObject
{
    /// <summary>Цвет <c>#RRGGBB</c>.</summary>
    public string Color { get; } = color;

    /// <summary>Выбран.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>Клавиша окна быстрого поиска и её действие.</summary>
/// <param name="Keys">Сочетание: «Ctrl+Enter».</param>
/// <param name="Action">Что делает.</param>
public sealed record KeyMapEntry(string Keys, string Action);

/// <summary>Вариант выбора в настройках: значение и подпись.</summary>
/// <typeparam name="T">Тип значения.</typeparam>
/// <param name="Value">Значение.</param>
/// <param name="Name">Подпись.</param>
public sealed record SettingOption<T>(T Value, string Name)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>
/// Значения настроек (PLAN.md, этап 9.1): загружаются из <see cref="ISettingsService"/>, каждое изменение сразу сохраняется
/// частичным изменением; отклонённое значение возвращается к сохранённому, причина — в <see cref="SettingsNote"/>.
/// </summary>
public sealed partial class SettingsViewModel
{
    private readonly ISettingsService _settings;
    private bool _loading;

    private static readonly SettingOption<BackdropMode>[] BackdropOptions =
    [
        new(BackdropMode.Mica, "Mica"),
        new(BackdropMode.Acrylic, "Acrylic"),
        new(BackdropMode.Solid, "Без эффекта"),
    ];

    private static readonly string[] AccentOptions =
    [
        "#0078D4", "#0063B1", "#8764B8", "#E3008C", "#E81123", "#F7630C", "#FFB900", "#107C10", "#00B7C3", "#69797E",
    ];

    private static readonly SettingOption<double>[] FontScaleOptions =
    [
        new(0.9, "90 %"),
        new(1.0, "100 %"),
        new(1.1, "110 %"),
        new(1.25, "125 %"),
        new(1.5, "150 %"),
    ];

    private static readonly SettingOption<PopupMonitor>[] MonitorOptions =
    [
        new(PopupMonitor.Cursor, "С курсором мыши"),
        new(PopupMonitor.ActiveWindow, "С активным окном"),
        new(PopupMonitor.Primary, "Основной"),
    ];

    private static readonly SettingOption<RankingPreset>[] PresetOptions =
    [
        new(RankingPreset.Balanced, "Сбалансированно"),
        new(RankingPreset.ByName, "По имени"),
        new(RankingPreset.Recent, "Недавнее выше"),
    ];

    private static readonly SettingOption<int>[] ReconcileOptions =
    [
        new(0, "Не сверять"),
        new(60, "Каждый час"),
        new(360, "Каждые 6 часов"),
        new(1_440, "Раз в сутки"),
    ];

    private static readonly SettingOption<string>[] LogLevelOptions =
    [
        new("Debug", "Подробно (Debug)"),
        new("Information", "Обычно (Information)"),
        new("Warning", "Предупреждения (Warning)"),
        new("Error", "Только ошибки (Error)"),
    ];

    /// <summary>Фоны окон.</summary>
    public IReadOnlyList<SettingOption<BackdropMode>> Backdrops { get; } = BackdropOptions;

    /// <summary>Свои акцентные цвета (как в «Параметрах» Windows).</summary>
    public IReadOnlyList<string> AccentColors { get; } = AccentOptions;

    /// <summary>Образцы акцентных цветов для выбора щелчком.</summary>
    public IReadOnlyList<AccentChoice> AccentChoices { get; } = [.. AccentOptions.Select(static color => new AccentChoice(color) { IsSelected = color == AccentOptions[0] })];

    /// <summary>Масштабы интерфейса.</summary>
    public IReadOnlyList<SettingOption<double>> FontScales { get; } = FontScaleOptions;

    /// <summary>Мониторы для окна поиска.</summary>
    public IReadOnlyList<SettingOption<PopupMonitor>> PopupMonitors { get; } = MonitorOptions;

    /// <summary>Пресеты ранжирования.</summary>
    public IReadOnlyList<SettingOption<RankingPreset>> RankingPresets { get; } = PresetOptions;

    /// <summary>Сверка индекса с диском.</summary>
    public IReadOnlyList<SettingOption<int>> ReconcileIntervals { get; } = ReconcileOptions;

    /// <summary>Уровни журнала.</summary>
    public IReadOnlyList<SettingOption<string>> LogLevels { get; } = LogLevelOptions;

    /// <summary>Клавиши окна быстрого поиска — из той же карты, что обрабатывает нажатия.</summary>
    public IReadOnlyList<KeyMapEntry> KeyMap { get; } =
    [
        new(QuickSearchKeyMap.OpenText, "Открыть"),
        new(QuickSearchKeyMap.RevealText, "Показать в Проводнике"),
        new(QuickSearchKeyMap.OpenWithText, "Открыть с помощью…"),
        new(QuickSearchKeyMap.PropertiesText, "Свойства"),
        new(QuickSearchKeyMap.RunAsAdminText, "Запустить от имени администратора"),
        new(QuickSearchKeyMap.CopyFilesText, "Копировать файл"),
        new(QuickSearchKeyMap.CopyPathText, "Копировать путь"),
        new(QuickSearchKeyMap.RenameText, "Переименовать"),
        new(QuickSearchKeyMap.DeleteText, "В корзину (с подтверждением)"),
        new(QuickSearchKeyMap.TogglePinText, "Закрепить или открепить"),
        new(QuickSearchKeyMap.EditTagsText, "Теги"),
        new("Ctrl+F", "Фильтры"),
        new("Tab", "Дополнить фильтр или открыть меню"),
        new("Ctrl+1…9", "Открыть строку с номером"),
        new("↑ в пустом поле", "Предыдущие запросы"),
        new("Esc", "Очистить запрос, затем закрыть"),
    ];

    /// <summary>Куда кладётся добавленное.</summary>
    public string InboxText => InboxPath.Length > 0 ? InboxPath : "Первый корень индекса — пока папка не выбрана";

    /// <summary>Сообщение о несохранённой настройке; пусто — всё сохранено.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSettingsNote))]
    public partial string SettingsNote { get; set; } = string.Empty;

    /// <summary>Есть сообщение о настройке.</summary>
    public bool HasSettingsNote => SettingsNote.Length > 0;

    /// <summary>Завершается, когда сохранено последнее изменение.</summary>
    public Task PendingSave { get; private set; } = Task.CompletedTask;

    /// <summary>Фон окон.</summary>
    [ObservableProperty]
    public partial SettingOption<BackdropMode> SelectedBackdrop { get; set; } = BackdropOptions[0];

    /// <summary>Свой акцентный цвет, когда системный выключен.</summary>
    [ObservableProperty]
    public partial string? SelectedAccentColor { get; set; } = AccentOptions[0];

    /// <summary>Масштаб интерфейса.</summary>
    [ObservableProperty]
    public partial SettingOption<double> SelectedFontScale { get; set; } = FontScaleOptions[1];

    /// <summary>Компактные списки.</summary>
    [ObservableProperty]
    public partial bool Compact { get; set; }

    /// <summary>Уменьшить движение.</summary>
    [ObservableProperty]
    public partial bool ReduceMotion { get; set; }

    /// <summary>Монитор для окна поиска.</summary>
    [ObservableProperty]
    public partial SettingOption<PopupMonitor> SelectedPopupMonitor { get; set; } = MonitorOptions[0];

    /// <summary>Прятать окно поиска при потере фокуса.</summary>
    [ObservableProperty]
    public partial bool HideOnDeactivate { get; set; }

    /// <summary>Папка в строках результатов.</summary>
    [ObservableProperty]
    public partial bool ShowPath { get; set; }

    /// <summary>Размер в строках результатов.</summary>
    [ObservableProperty]
    public partial bool ShowSize { get; set; }

    /// <summary>Дата в строках результатов.</summary>
    [ObservableProperty]
    public partial bool ShowDate { get; set; }

    /// <summary>Нечёткий поиск.</summary>
    [ObservableProperty]
    public partial bool Fuzzy { get; set; }

    /// <summary>Подборка при пустом запросе.</summary>
    [ObservableProperty]
    public partial bool ShowSuggestionsOnEmpty { get; set; }

    /// <summary>Пресет ранжирования.</summary>
    [ObservableProperty]
    public partial SettingOption<RankingPreset> SelectedRankingPreset { get; set; } = PresetOptions[0];

    /// <summary>Кандидатов до ранжирования.</summary>
    [ObservableProperty]
    public partial decimal CandidateLimit { get; set; }

    /// <summary>Запросов в истории.</summary>
    [ObservableProperty]
    public partial decimal HistoryLimit { get; set; }

    /// <summary>Запускаться свёрнутым в трей.</summary>
    [ObservableProperty]
    public partial bool StartMinimized { get; set; }

    /// <summary>Спрашивать при выходе во время операций.</summary>
    [ObservableProperty]
    public partial bool ConfirmExitDuringOperations { get; set; }

    /// <summary>Скрытые файлы у новых корней.</summary>
    [ObservableProperty]
    public partial bool IncludeHiddenByDefault { get; set; }

    /// <summary>Системные файлы у новых корней.</summary>
    [ObservableProperty]
    public partial bool IncludeSystemByDefault { get; set; }

    /// <summary>Заходить в junction и символические ссылки.</summary>
    [ObservableProperty]
    public partial bool FollowReparsePoints { get; set; }

    /// <summary>Сверка с диском.</summary>
    [ObservableProperty]
    public partial SettingOption<int> SelectedReconcileInterval { get; set; } = ReconcileOptions[2];

    /// <summary>Папка «Входящие»; пусто — первый корень.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InboxText))]
    public partial string InboxPath { get; set; } = string.Empty;

    /// <summary>Шаблон имени файла из буфера обмена.</summary>
    [ObservableProperty]
    public partial string ClipboardNameTemplate { get; set; } = string.Empty;

    /// <summary>Уровень журнала.</summary>
    [ObservableProperty]
    public partial SettingOption<string> SelectedLogLevel { get; set; } = LogLevelOptions[1];

    /// <summary>Сочетание Quick Switch из настроек.</summary>
    [ObservableProperty]
    public partial string QuickSwitchHotkey { get; set; } = string.Empty;

    /// <summary>Сочетание быстрого поиска из настроек.</summary>
    [ObservableProperty]
    public partial string QuickSearchHotkey { get; set; } = string.Empty;

    /// <summary>
    /// Перечитывает настройки и показывает их, ничего не сохраняя.
    /// </summary>
    public async Task LoadSettingsAsync()
    {
        try
        {
            Apply(await _settings.GetAsync());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SettingsNote = "Не удалось загрузить настройки: " + exception.Message;
        }
    }

    /// <summary>Показывает настройки без сохранения.</summary>
    private void Apply(SettingsDto settings)
    {
        _loading = true;
        try
        {
            var theme = settings.Appearance.Theme switch
            {
                ThemeMode.Light => AppTheme.Light,
                ThemeMode.Dark => AppTheme.Dark,
                _ => AppTheme.System,
            };
            SelectedTheme = Themes.First(option => option.Theme == theme);
            SelectedBackdrop = Backdrops.FirstOrDefault(option => option.Value == settings.Appearance.Backdrop) ?? Backdrops[0];
            UseBackdropEffects = settings.Appearance.Backdrop != BackdropMode.Solid;
            UseSystemAccent = settings.Appearance.UseSystemAccent;
            SelectedAccentColor = settings.Appearance.AccentColor ?? AccentColors[0];
            SelectedFontScale = FontScales.MinBy(option => Math.Abs(option.Value - settings.Appearance.FontScale)) ?? FontScales[1];
            Compact = settings.Appearance.Compact;
            ReduceMotion = settings.Appearance.ReduceMotion;

            SelectedPopupMonitor = PopupMonitors.FirstOrDefault(option => option.Value == settings.Popup.Monitor) ?? PopupMonitors[0];
            HideOnDeactivate = settings.Popup.HideOnDeactivate;
            ShowPath = settings.Popup.ShowPath;
            ShowSize = settings.Popup.ShowSize;
            ShowDate = settings.Popup.ShowDate;
            QuickSearchHotkey = settings.Hotkeys.QuickSearch;
            QuickSwitchHotkey = settings.Hotkeys.QuickSwitch;

            Fuzzy = settings.Search.Fuzzy;
            ShowSuggestionsOnEmpty = settings.Search.ShowSuggestionsOnEmpty;
            SelectedRankingPreset = RankingPresets.FirstOrDefault(option => option.Value == settings.Search.RankingPreset) ?? RankingPresets[0];
            CandidateLimit = settings.Search.CandidateLimit;
            HistoryLimit = settings.Search.HistoryLimit;

            StartMinimized = settings.Tray.StartMinimized;
            CloseToTray = settings.Tray.CloseToTray;
            ConfirmExitDuringOperations = settings.Tray.ConfirmExitDuringOperations;

            IncludeHiddenByDefault = settings.Indexing.IncludeHiddenByDefault;
            IncludeSystemByDefault = settings.Indexing.IncludeSystemByDefault;
            FollowReparsePoints = settings.Indexing.FollowReparsePoints;
            SelectedReconcileInterval = ReconcileIntervals.FirstOrDefault(option => option.Value == settings.Indexing.ReconcileIntervalMinutes)
                ?? new SettingOption<int>(settings.Indexing.ReconcileIntervalMinutes, $"Каждые {settings.Indexing.ReconcileIntervalMinutes} мин");
            ApplyExclusions(settings.Indexing.Exclusions);

            InboxPath = settings.Add.InboxPath ?? string.Empty;
            ClipboardNameTemplate = settings.Add.ClipboardNameTemplate;
            SelectedLogLevel = LogLevels.FirstOrDefault(option => option.Value == settings.Diagnostics.LogLevel) ?? LogLevels[1];
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Сохраняет одно поле: <c>{"section":{"key":value}}</c>.</summary>
    private void Save(string section, string key, JsonNode? value)
    {
        if (_loading)
        {
            return;
        }

        PendingSave = SaveAsync(new JsonObject { [section] = new JsonObject { [key] = value } });
    }

    private async Task SaveAsync(JsonObject patch)
    {
        // Изменения сохраняются по очереди: следующее ждёт предыдущее, иначе быстрые переключения могли бы обогнать друг друга.
        var previous = PendingSave;
        await previous;
        try
        {
            await _settings.PatchAsync(patch);
            SettingsNote = string.Empty;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SettingsNote = "Настройка не сохранена: " + exception.Message;
            await LoadSettingsAsync();
        }
    }

    partial void OnSelectedBackdropChanged(SettingOption<BackdropMode> value)
    {
        UseBackdropEffects = value.Value != BackdropMode.Solid;
        Save("appearance", "backdrop", value.Value.ToString());
    }

    partial void OnUseSystemAccentChanged(bool value) => Save("appearance", "useSystemAccent", value);

    partial void OnSelectedAccentColorChanged(string? value)
    {
        foreach (var choice in AccentChoices)
        {
            choice.IsSelected = string.Equals(choice.Color, value, StringComparison.OrdinalIgnoreCase);
        }

        if (value is not null)
        {
            Save("appearance", "accentColor", value);
        }
    }

    /// <summary>Выбрать свой акцентный цвет.</summary>
    [RelayCommand]
    private void SelectAccent(AccentChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        SelectedAccentColor = choice.Color;
    }

    partial void OnSelectedFontScaleChanged(SettingOption<double> value) => Save("appearance", "fontScale", value.Value);

    partial void OnCompactChanged(bool value) => Save("appearance", "compact", value);

    partial void OnReduceMotionChanged(bool value) => Save("appearance", "reduceMotion", value);

    partial void OnSelectedPopupMonitorChanged(SettingOption<PopupMonitor> value) => Save("popup", "monitor", value.Value.ToString());

    partial void OnHideOnDeactivateChanged(bool value) => Save("popup", "hideOnDeactivate", value);

    partial void OnShowPathChanged(bool value) => Save("popup", "showPath", value);

    partial void OnShowSizeChanged(bool value) => Save("popup", "showSize", value);

    partial void OnShowDateChanged(bool value) => Save("popup", "showDate", value);

    partial void OnFuzzyChanged(bool value) => Save("search", "fuzzy", value);

    partial void OnShowSuggestionsOnEmptyChanged(bool value) => Save("search", "showSuggestionsOnEmpty", value);

    partial void OnSelectedRankingPresetChanged(SettingOption<RankingPreset> value) => Save("search", "rankingPreset", value.Value.ToString());

    partial void OnCandidateLimitChanged(decimal value) => Save("search", "candidateLimit", (int)value);

    partial void OnHistoryLimitChanged(decimal value) => Save("search", "historyLimit", (int)value);

    partial void OnStartMinimizedChanged(bool value) => Save("tray", "startMinimized", value);

    partial void OnCloseToTrayChanged(bool value) => Save("tray", "closeToTray", value);

    partial void OnConfirmExitDuringOperationsChanged(bool value) => Save("tray", "confirmExitDuringOperations", value);

    partial void OnIncludeHiddenByDefaultChanged(bool value) => Save("indexing", "includeHiddenByDefault", value);

    partial void OnIncludeSystemByDefaultChanged(bool value) => Save("indexing", "includeSystemByDefault", value);

    partial void OnFollowReparsePointsChanged(bool value) => Save("indexing", "followReparsePoints", value);

    partial void OnSelectedReconcileIntervalChanged(SettingOption<int> value) => Save("indexing", "reconcileIntervalMinutes", value.Value);

    partial void OnClipboardNameTemplateChanged(string value)
    {
        if (value.Trim().Length > 0)
        {
            Save("add", "clipboardNameTemplate", value.Trim());
        }
    }

    partial void OnSelectedLogLevelChanged(SettingOption<string> value) => Save("diagnostics", "logLevel", value.Value);

    /// <summary>Выбрать папку «Входящие».</summary>
    [RelayCommand]
    private async Task PickInboxAsync()
    {
        if (_dialogs is null || await _dialogs.PickFolderAsync("Папка «Входящие»") is not { } folder)
        {
            return;
        }

        InboxPath = folder;
        Save("add", "inboxPath", folder);
    }

    /// <summary>«Входящие» — снова первый корень.</summary>
    [RelayCommand]
    private void ResetInbox()
    {
        InboxPath = string.Empty;
        Save("add", "inboxPath", null);
    }

    /// <summary>Очистить историю запросов.</summary>
    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        if (_dialogs is not null && !await _dialogs.ConfirmAsync("Очистить историю", "Удалить все запросы из истории поиска?", "Очистить"))
        {
            return;
        }

        try
        {
            await _search.ClearHistoryAsync(CancellationToken.None);
            SettingsNote = string.Empty;
            _notices?.Show("История очищена", "Запросы больше не подсказываются.", FluentAvalonia.UI.Controls.FAInfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SettingsNote = "Историю не удалось очистить: " + exception.Message;
        }
    }

}

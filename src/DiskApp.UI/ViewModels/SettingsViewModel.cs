using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using DiskApp.UI.Localization;
using DiskApp.UI.Services;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.ViewModels;

/// <summary>
/// Вариант темы для выбора в настройках.
/// </summary>
/// <param name="Theme">Тема.</param>
/// <param name="Name">Подпись.</param>
public sealed record ThemeOption(AppTheme Theme, string Name)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>
/// Страница настроек в стиле «Параметров» Windows 11. Тема применяется сразу; остальные переключатели пока
/// только показывают будущие настройки — их сохранение и действие появятся в этапах 6.5, 6.6 и 9.
/// </summary>
public sealed partial class SettingsViewModel : PageViewModel, IPageLifecycle
{
    private readonly IThemeService _themes;
    private readonly ISearchService _search;
    private readonly IAutoStartService _autoStart;
    private readonly IShellIntegrationService _integration;
    private readonly IQuickSwitchSettings _quickSwitch;
    private readonly IDialogService? _dialogs;
    private readonly IAppCommands? _commands;
    private readonly IInfoBarService? _notices;

    /// <summary>
    /// Создаёт страницу и запускает загрузку корней индекса (<see cref="Initialization"/>).
    /// </summary>
    public SettingsViewModel(
        IThemeService themes,
        ISearchService search,
        HotkeyStatus hotkeys,
        IAutoStartService? autoStart = null,
        IShellIntegrationService? integration = null,
        IQuickSwitchSettings? quickSwitch = null,
        ISettingsService? settings = null,
        IDialogService? dialogs = null,
        IInfoBarService? notices = null,
        IRootsService? roots = null,
        IAppMaintenance? maintenance = null,
        IHotkeyChecker? hotkeyChecker = null,
        IAppCommands? commands = null)
        : base(PageKeys.Settings, "Settings.Settings", FASymbol.Settings)
    {
        ArgumentNullException.ThrowIfNull(themes);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(hotkeys);

        // Пока страница заполняется значениями по умолчанию, ничего не сохраняется: настоящие значения придут из LoadSettingsAsync.
        _loading = true;
        _settings = settings ?? new SampleSettingsService();
        _dialogs = dialogs;
        _notices = notices;
        _roots = roots ?? new SampleRootsService();
        _maintenance = maintenance ?? new NullAppMaintenance();
        _hotkeyChecker = hotkeyChecker;
        _commands = commands;
        _themes = themes;
        _search = search;
        _autoStart = autoStart ?? new NullAutoStartService();
        _integration = integration ?? new NullShellIntegrationService();
        IsIntegrationSupported = _integration.IsSupported;
        SendToEnabled = _integration.IsEnabled(ShellIntegrationFeature.SendTo);
        ContextMenuEnabled = _integration.IsEnabled(ShellIntegrationFeature.ContextMenu);
        ProtocolEnabled = _integration.IsEnabled(ShellIntegrationFeature.Protocol);
        DesktopShortcutEnabled = _integration.IsEnabled(ShellIntegrationFeature.DesktopShortcut);
        _quickSwitch = quickSwitch ?? new NullQuickSwitchSettings();
        IsQuickSwitchSupported = _quickSwitch.IsSupported;
        QuickSwitchEnabled = _quickSwitch.IsEnabled;
        QuickSwitchDescription = L.F("Settings.ExperimentalInTheSaveOrOpen", _quickSwitch.HotkeyText)
            + L.T("Settings.ChooseAFolderInDiskAppAnd");
        Hotkeys = hotkeys;

        Themes =
        [
            new ThemeOption(AppTheme.System, L.T("Settings.AsInWindows")),
            new ThemeOption(AppTheme.Light, L.T("Settings.Light")),
            new ThemeOption(AppTheme.Dark, L.T("Settings.Dark")),
        ];
        SelectedTheme = Themes.First(option => option.Theme == themes.Current);

        UseSystemAccent = true;
        UseBackdropEffects = true;
        IsAutoStartSupported = _autoStart.IsSupported;
        StartWithWindows = _autoStart.IsEnabled;
        AutoStartNote = _autoStart.Warning;
        CloseToTray = true;

        VersionText = L.F("Settings.VersionAlpha", AppVersion.Current);
        Categories =
        [
            new SettingsCategory("appearance", L.T("Settings.Appearance"), FASymbol.ColorBackground),
            new SettingsCategory("hotkeys", L.T("Settings.Hotkeys"), FASymbol.Keyboard),
            new SettingsCategory("quick", L.T("Settings.QuickSearch"), FASymbol.View),
            new SettingsCategory("search", L.T("Settings.Search"), FASymbol.Find),
            new SettingsCategory("index", L.T("Settings.Index"), FASymbol.Folder),
            new SettingsCategory("behavior", L.T("Settings.Behavior"), FASymbol.Settings),
            new SettingsCategory("integrations", L.T("Settings.Integrations"), FASymbol.Link),
            new SettingsCategory("inbox", L.T("Settings.Inbox"), FASymbol.Download),
            new SettingsCategory("notifications", L.T("Settings.Notifications"), FASymbol.Clock),
            new SettingsCategory("data", L.T("Settings.Data"), FASymbol.Library),
            new SettingsCategory(LogsCategory, L.T("Settings.Logs"), FASymbol.List),
            new SettingsCategory("about", L.T("Settings.About"), FASymbol.Help),
        ];
        SelectedCategory = Categories[0];
        SelectedLogFilter = LogFilters[1];
        _loading = false;
        Initialization = InitializeAsync();
    }

    /// <summary>Завершается, когда загружены корни индекса и настройки.</summary>
    public Task Initialization { get; }

    /// <summary>Разделы настроек слева, как в «Параметрах» Windows.</summary>
    public IReadOnlyList<SettingsCategory> Categories { get; }

    /// <summary>Открытый раздел.</summary>
    [ObservableProperty]
    public partial SettingsCategory SelectedCategory { get; set; }

    /// <summary>Вход на страницу: значения перечитываются — их могли поменять в другом окне или в файле настроек.</summary>
    public void OnNavigatedTo() => _ = InitializeAsync();

    /// <inheritdoc />
    public void OnNavigatedFrom()
    {
    }

    /// <inheritdoc />
    protected override Task RelocalizeAsync() => InitializeAsync();

    private async Task InitializeAsync()
    {
        await RefreshAsync();
        await LoadSettingsAsync();
        await RefreshDataAsync();
    }

    /// <summary>Доступные темы.</summary>
    public IReadOnlyList<ThemeOption> Themes { get; }

    /// <summary>Выбранная тема; применяется немедленно.</summary>
    [ObservableProperty]
    public partial ThemeOption SelectedTheme { get; set; }

    /// <summary>Использовать акцентный цвет системы.</summary>
    [ObservableProperty]
    public partial bool UseSystemAccent { get; set; }

    /// <summary>Эффекты Mica и Acrylic.</summary>
    [ObservableProperty]
    public partial bool UseBackdropEffects { get; set; }

    /// <summary>Запускать вместе с Windows.</summary>
    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    /// <summary>Переключатель автозапуска доступен.</summary>
    public bool IsAutoStartSupported { get; }

    /// <summary>Предупреждение или ошибка автозапуска; пусто — нечего сказать.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAutoStartNote))]
    public partial string AutoStartNote { get; set; }

    /// <summary>Есть пояснение к автозапуску.</summary>
    public bool HasAutoStartNote => AutoStartNote.Length > 0;

    /// <summary>Закрывать главное окно в трей.</summary>
    [ObservableProperty]
    public partial bool CloseToTray { get; set; }

    /// <summary>Действующие глобальные сочетания: быстрый поиск, главное окно, окно Drag&amp;Drop (ADR-0006, ADR-0011).</summary>
    public HotkeyStatus Hotkeys { get; }

    /// <summary>Расписание напоминаний.</summary>
    public string ReminderText { get; } = L.T("Settings.DailyAt1800AndWhen");

    /// <summary>Версия приложения.</summary>
    public string VersionText { get; }

    /// <summary>Ссылка на репозиторий.</summary>
    public Uri RepositoryUri { get; } = new("https://github.com/IichiKohuro/DiskApp");

    /// <summary>Корни индекса.</summary>
    public ObservableCollection<string> Roots { get; } = [];

    /// <summary>
    /// Перечитывает корни индекса.
    /// </summary>
    public async Task RefreshAsync()
    {
        try
        {
            await LoadRootsAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SettingsNote = L.T("Settings.CouldNotLoadTheRoots") + exception.Message;
        }
    }

    /// <summary>Интеграции с Проводником поддерживаются.</summary>
    public bool IsIntegrationSupported { get; }

    /// <summary>Ярлык «DiskApp» в «Отправить».</summary>
    [ObservableProperty]
    public partial bool SendToEnabled { get; set; }

    /// <summary>Пункты контекстного меню Проводника.</summary>
    [ObservableProperty]
    public partial bool ContextMenuEnabled { get; set; }

    /// <summary>Ссылки <c>diskapp://</c>.</summary>
    [ObservableProperty]
    public partial bool ProtocolEnabled { get; set; }

    /// <summary>Ошибка изменения интеграции; пусто — всё хорошо.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIntegrationNote))]
    public partial string IntegrationNote { get; set; } = string.Empty;

    /// <summary>Есть ли сообщение об интеграции.</summary>
    public bool HasIntegrationNote => IntegrationNote.Length > 0;

    partial void OnSendToEnabledChanged(bool value) => ApplyIntegration(ShellIntegrationFeature.SendTo, value, enabled => SendToEnabled = enabled);

    partial void OnContextMenuEnabledChanged(bool value) => ApplyIntegration(ShellIntegrationFeature.ContextMenu, value, enabled => ContextMenuEnabled = enabled);

    partial void OnProtocolEnabledChanged(bool value) => ApplyIntegration(ShellIntegrationFeature.Protocol, value, enabled => ProtocolEnabled = enabled);

    /// <summary>Ярлык на рабочем столе.</summary>
    [ObservableProperty]
    public partial bool DesktopShortcutEnabled { get; set; }

    partial void OnDesktopShortcutEnabledChanged(bool value) => ApplyIntegration(ShellIntegrationFeature.DesktopShortcut, value, enabled => DesktopShortcutEnabled = enabled);

    /// <summary>Quick Switch поддерживается.</summary>
    public bool IsQuickSwitchSupported { get; }

    /// <summary>Описание Quick Switch с действующим сочетанием клавиш.</summary>
    public string QuickSwitchDescription { get; }

    /// <summary>Quick Switch включён.</summary>
    [ObservableProperty]
    public partial bool QuickSwitchEnabled { get; set; }

    partial void OnQuickSwitchEnabledChanged(bool value)
    {
        if (_quickSwitch is null || !_quickSwitch.IsSupported || value == _quickSwitch.IsEnabled)
        {
            return;
        }

        try
        {
            _quickSwitch.SetEnabled(value);
            IntegrationNote = string.Empty;
        }
        catch (InvalidOperationException exception)
        {
            IntegrationNote = exception.Message;
            QuickSwitchEnabled = _quickSwitch.IsEnabled;
        }
    }

    private void ApplyIntegration(ShellIntegrationFeature feature, bool value, Action<bool> restore)
    {
        // Значение из конструктора совпадает с системой: менять нечего.
        if (_integration is null || !_integration.IsSupported || value == _integration.IsEnabled(feature))
        {
            return;
        }

        try
        {
            _integration.SetEnabled(feature, value);
            IntegrationNote = string.Empty;
        }
        catch (InvalidOperationException exception)
        {
            IntegrationNote = exception.Message;
            restore(_integration.IsEnabled(feature));
        }
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_autoStart is null || value == _autoStart.IsEnabled || !_autoStart.IsSupported)
        {
            return;
        }

        try
        {
            _autoStart.SetEnabled(value);
            AutoStartNote = _autoStart.Warning;
        }
        catch (InvalidOperationException exception)
        {
            AutoStartNote = exception.Message;
            StartWithWindows = _autoStart.IsEnabled;
        }
    }

    partial void OnSelectedThemeChanged(ThemeOption value)
    {
        // Начальный выбор и загрузка настроек не применяют тему: её применяет оболочка по сохранённой настройке.
        if (!_loading && value.Theme != _themes.Current)
        {
            _themes.Apply(value.Theme);
        }

        Save("appearance", "theme", value.Theme.ToString());
    }

}

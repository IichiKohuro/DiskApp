using System.Collections.ObjectModel;
using System.Reflection;

using CommunityToolkit.Mvvm.ComponentModel;

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
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly IThemeService _themes;
    private readonly ISearchService _search;
    private readonly IAutoStartService _autoStart;
    private readonly IShellIntegrationService _integration;
    private readonly IQuickSwitchSettings _quickSwitch;

    /// <summary>
    /// Создаёт страницу и запускает загрузку корней индекса (<see cref="Initialization"/>).
    /// </summary>
    public SettingsViewModel(
        IThemeService themes,
        ISearchService search,
        HotkeyStatus hotkeys,
        IAutoStartService? autoStart = null,
        IShellIntegrationService? integration = null,
        IQuickSwitchSettings? quickSwitch = null)
        : base(PageKeys.Settings, "Настройки", FASymbol.Settings)
    {
        ArgumentNullException.ThrowIfNull(themes);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(hotkeys);

        _themes = themes;
        _search = search;
        _autoStart = autoStart ?? new NullAutoStartService();
        _integration = integration ?? new NullShellIntegrationService();
        IsIntegrationSupported = _integration.IsSupported;
        SendToEnabled = _integration.IsEnabled(ShellIntegrationFeature.SendTo);
        ContextMenuEnabled = _integration.IsEnabled(ShellIntegrationFeature.ContextMenu);
        ProtocolEnabled = _integration.IsEnabled(ShellIntegrationFeature.Protocol);
        _quickSwitch = quickSwitch ?? new NullQuickSwitchSettings();
        IsQuickSwitchSupported = _quickSwitch.IsSupported;
        QuickSwitchEnabled = _quickSwitch.IsEnabled;
        QuickSwitchDescription = $"Экспериментально. {_quickSwitch.HotkeyText} в окне «Сохранить» или «Открыть» любой программы — "
            + "выбор папки в DiskApp, и диалог переходит в неё";
        Hotkeys = hotkeys;

        Themes =
        [
            new ThemeOption(AppTheme.System, "Как в системе"),
            new ThemeOption(AppTheme.Light, "Светлая"),
            new ThemeOption(AppTheme.Dark, "Тёмная"),
        ];
        SelectedTheme = Themes.First(option => option.Theme == themes.Current);

        UseSystemAccent = true;
        UseBackdropEffects = true;
        ShowOnCursorScreen = true;
        IsAutoStartSupported = _autoStart.IsSupported;
        StartWithWindows = _autoStart.IsEnabled;
        AutoStartNote = _autoStart.Warning;
        CloseToTray = true;
        RemindAboutInbox = true;

        VersionText = $"Версия {ReadVersion()} · альфа";
        Initialization = RefreshAsync();
    }

    /// <summary>Завершается, когда корни индекса загружены.</summary>
    public Task Initialization { get; }

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

    /// <summary>Показывать окно поиска на мониторе с курсором.</summary>
    [ObservableProperty]
    public partial bool ShowOnCursorScreen { get; set; }

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

    /// <summary>Напоминать о разборе входящих.</summary>
    [ObservableProperty]
    public partial bool RemindAboutInbox { get; set; }

    /// <summary>Действующее сочетание быстрого поиска (решение 15 плана, ADR-0006; настраиваемым станет в этапе 6.5).</summary>
    public HotkeyStatus Hotkeys { get; }

    /// <summary>Горячая клавиша разбора входящих.</summary>
    public string InboxHotkeyText { get; } = "Ctrl + Alt + I";

    /// <summary>Расписание напоминаний.</summary>
    public string ReminderText { get; } = "Ежедневно в 18:00 и когда накопится 10 файлов";

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
        var status = await _search.GetIndexStatusAsync(CancellationToken.None);
        Roots.Clear();
        foreach (var root in status.Roots)
        {
            Roots.Add(root);
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
        // Начальный выбор в конструкторе совпадает с текущей темой — не дёргаем приложение без нужды.
        if (value.Theme != _themes.Current)
        {
            _themes.Apply(value.Theme);
        }
    }

    private static string ReadVersion()
    {
        var informational = typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
        {
            return typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? informational[..plus] : informational;
    }
}

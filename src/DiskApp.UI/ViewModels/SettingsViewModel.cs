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

    /// <summary>
    /// Создаёт страницу и запускает загрузку корней индекса (<see cref="Initialization"/>).
    /// </summary>
    public SettingsViewModel(IThemeService themes, ISearchService search, HotkeyStatus hotkeys)
        : base(PageKeys.Settings, "Настройки", FASymbol.Settings)
    {
        ArgumentNullException.ThrowIfNull(themes);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(hotkeys);

        _themes = themes;
        _search = search;
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
        StartWithWindows = false;
        CloseToTray = true;
        RemindAboutInbox = true;

        VersionText = $"Версия {ReadVersion()} · прототип интерфейса на макетных данных";
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

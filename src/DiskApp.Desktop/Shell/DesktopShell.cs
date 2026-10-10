using System.ComponentModel;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Platform;

using DiskApp.Desktop.Platform.Windows;
using DiskApp.Desktop.Windows;
using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

using FluentAvalonia.UI.Controls;

using Microsoft.Extensions.DependencyInjection;

using Serilog;

namespace DiskApp.Desktop.Shell;

/// <summary>
/// Оболочка десктопа: иконка в трее с меню, глобальная горячая клавиша, главное окно (создаётся при первом
/// обращении) и заранее созданное окно быстрого поиска. Реализует <see cref="IAppCommands"/> для интерфейса.
/// </summary>
internal sealed class DesktopShell : IAppCommands, IDisposable
{
    private const int SearchHotkeyId = 1;
    private const string IconUri = "avares://DiskApp/Assets/DiskApp.ico";

    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly IServiceProvider _services;
    private readonly ILogger _log;

    private TrayIcon? _tray;
    private MessageWindow? _messageWindow;
    private GlobalHotkey? _hotkey;
    private string? _hotkeyError;
    private MainWindow? _mainWindow;
    private QuickSearchWindow? _quickSearch;
    private bool _exiting;

    /// <summary>
    /// Создаёт оболочку; окна и трей появляются в <see cref="Start"/>.
    /// </summary>
    public DesktopShell(IClassicDesktopStyleApplicationLifetime lifetime, IServiceProvider services, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(log);

        _lifetime = lifetime;
        _services = services;
        _log = log.ForContext<DesktopShell>();
    }

    /// <summary>
    /// Поднимает трей, хоткей и окно поиска; показывает главное окно, если не передан <c>--minimized</c>.
    /// Вызывать на UI-потоке после настройки Avalonia.
    /// </summary>
    public void Start(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var startup = StartupArguments.Parse(args);
        var searchViewModel = _services.GetRequiredService<QuickSearchViewModel>();
        _quickSearch = new QuickSearchWindow { DataContext = searchViewModel };

        CreateTray();
        RegisterHotkey();

        if (!startup.Minimized || startup.Show || startup.OpenSettings)
        {
            ShowMainWindow();
        }

        if (startup.OpenSettings)
        {
            NavigateTo(PageKeys.Settings);
        }

        if (startup.ShowSearch)
        {
            searchViewModel.Query = startup.SearchQuery ?? string.Empty;
            ShowQuickSearch();
        }

        _log.Information("Оболочка запущена: трей, хоткей {HotkeyState}, аргументы {Arguments}",
            _hotkey is null ? "не зарегистрирован" : "Ctrl+Alt+Space",
            startup);
    }

    /// <inheritdoc />
    public void ShowQuickSearch() => _quickSearch?.ShowAt(CursorPosition());

    /// <inheritdoc />
    public void ShowMainWindow()
    {
        _mainWindow ??= CreateMainWindow();

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Show();
        _mainWindow.Activate();
    }

    /// <inheritdoc />
    public void NavigateTo(string pageKey)
    {
        ShowMainWindow();
        _services.GetRequiredService<MainViewModel>().NavigateTo(pageKey);
    }

    /// <summary>
    /// Завершает приложение: закрывает окна, снимает хоткей и трей, останавливает цикл сообщений.
    /// </summary>
    public void Exit()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _log.Information("Выход по команде пользователя");

        if (_mainWindow is not null)
        {
            _mainWindow.AllowClose = true;
            _mainWindow.Close();
        }

        if (_quickSearch is not null)
        {
            _quickSearch.AllowClose = true;
            _quickSearch.Close();
        }

        Dispose();
        _lifetime.Shutdown();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            _hotkey?.Dispose();
            _hotkey = null;
            _messageWindow?.Dispose();
            _messageWindow = null;
        }

        _tray?.Dispose();
        _tray = null;
    }

    private static PixelPoint? CursorPosition() => OperatingSystem.IsWindows() ? WindowEffects.GetCursorPosition() : null;

    private void CreateTray()
    {
        var menu = new NativeMenu();

        var open = new NativeMenuItem("Открыть DiskApp");
        open.Click += (_, _) => ShowMainWindow();
        menu.Items.Add(open);

        var search = new NativeMenuItem("Быстрый поиск") { Gesture = new KeyGesture(Key.Space, KeyModifiers.Control | KeyModifiers.Alt) };
        search.Click += (_, _) => ShowQuickSearch();
        menu.Items.Add(search);

        menu.Items.Add(new NativeMenuItemSeparator());

        var settings = new NativeMenuItem("Настройки");
        settings.Click += (_, _) => NavigateTo(PageKeys.Settings);
        menu.Items.Add(settings);

        menu.Items.Add(new NativeMenuItemSeparator());

        var exit = new NativeMenuItem("Выход");
        exit.Click += (_, _) => Exit();
        menu.Items.Add(exit);

        _tray = new TrayIcon
        {
            Icon = LoadIcon(),
            ToolTipText = "DiskApp — индекс актуален",
            Menu = menu,
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => ToggleQuickSearch();

        if (Application.Current is { } application)
        {
            TrayIcon.SetIcons(application, [_tray]);
        }
    }

    private void RegisterHotkey()
    {
        if (!OperatingSystem.IsWindows())
        {
            _hotkeyError = "глобальные горячие клавиши пока только для Windows";
            return;
        }

        try
        {
            _messageWindow = new MessageWindow();
            _hotkey = GlobalHotkey.Register(_messageWindow, SearchHotkeyId, NativeMethods.ModControl | NativeMethods.ModAlt, NativeMethods.VkSpace);
            _hotkey.Pressed += (_, _) => ToggleQuickSearch();
        }
        catch (Win32Exception exception)
        {
            _hotkeyError = exception.Message;
            _log.Warning(exception, "Горячая клавиша Ctrl+Alt+Space не зарегистрирована");
        }
    }

    private void ToggleQuickSearch() => _quickSearch?.Toggle(CursorPosition());

    private MainWindow CreateMainWindow()
    {
        var viewModel = _services.GetRequiredService<MainViewModel>();
        var window = new MainWindow { DataContext = viewModel };
        _lifetime.MainWindow = window;

        if (_hotkeyError is not null)
        {
            viewModel.ShowNotice(
                "Горячая клавиша недоступна",
                $"Ctrl + Alt + Space не зарегистрирована: {_hotkeyError} Поиск открывается щелчком по иконке в трее.",
                FAInfoBarSeverity.Warning);
        }

        return window;
    }

    private WindowIcon? LoadIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(IconUri));
            return new WindowIcon(stream);
        }
        catch (Exception exception)
        {
            _log.Warning(exception, "Иконка трея не загружена из {Uri}", IconUri);
            return null;
        }
    }
}

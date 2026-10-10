using System.ComponentModel;
using System.Runtime.Versioning;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;

using DiskApp.Desktop.Platform.Windows;
using DiskApp.Desktop.SingleInstance;
using DiskApp.Desktop.Startup;
using DiskApp.Desktop.Windows;
using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

using FluentAvalonia.UI.Controls;

using Microsoft.Extensions.DependencyInjection;

using Serilog;

namespace DiskApp.Desktop.Shell;

/// <summary>
/// Оболочка десктопа: иконка в трее с меню, глобальная горячая клавиша, приём аргументов от вторых экземпляров,
/// главное окно (создаётся при первом обращении) и заранее созданное окно быстрого поиска.
/// Реализует <see cref="IAppCommands"/> для интерфейса.
/// </summary>
internal sealed class DesktopShell : IAppCommands, IDisposable
{
    private const int SearchHotkeyId = 1;
    private const string IconUri = "avares://DiskApp/Assets/DiskApp.ico";

    /// <summary>
    /// Сочетания для быстрого поиска по убыванию предпочтения (ADR-0006): регистрируется первое свободное.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static HotkeyCandidate[] QuickSearchHotkeys() =>
    [
        new(NativeMethods.ModControl | NativeMethods.ModAlt, NativeMethods.VkD, "Ctrl + Alt + D", new KeyGesture(Key.D, KeyModifiers.Control | KeyModifiers.Alt)),
        new(NativeMethods.ModControl | NativeMethods.ModAlt, NativeMethods.VkF, "Ctrl + Alt + F", new KeyGesture(Key.F, KeyModifiers.Control | KeyModifiers.Alt)),
        new(NativeMethods.ModControl | NativeMethods.ModAlt, NativeMethods.VkSpace, "Ctrl + Alt + Space", new KeyGesture(Key.Space, KeyModifiers.Control | KeyModifiers.Alt)),
    ];

    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly IServiceProvider _services;
    private readonly InstanceNames _names;
    private readonly ILogger _log;

    private TrayIcon? _tray;
    private MessageWindow? _messageWindow;
    private GlobalHotkey? _hotkey;
    private HotkeyCandidate? _activeHotkey;
    private InstanceServer? _instanceServer;
    private MainWindow? _mainWindow;
    private QuickSearchWindow? _quickSearch;
    private bool _exiting;

    /// <summary>
    /// Создаёт оболочку; окна, трей и приём аргументов появляются в <see cref="Start"/>.
    /// </summary>
    public DesktopShell(IClassicDesktopStyleApplicationLifetime lifetime, IServiceProvider services, InstanceNames names, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(log);

        _lifetime = lifetime;
        _services = services;
        _names = names;
        _log = log.ForContext<DesktopShell>();
    }

    /// <summary>
    /// Поднимает трей, хоткей, окно поиска и сервер одиночного экземпляра; применяет аргументы запуска.
    /// Вызывать на UI-потоке после настройки Avalonia.
    /// </summary>
    public void Start(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var startup = StartupArguments.Parse(args);
        _quickSearch = new QuickSearchWindow { DataContext = _services.GetRequiredService<QuickSearchViewModel>() };

        RegisterHotkey();
        CreateTray();
        _log.Information("Трей показан через {StartupMs} мс после старта процесса", StartupTimer.Milliseconds);

        _instanceServer = new InstanceServer(_names.Pipe, OnSecondInstanceArguments, _log);
        _instanceServer.Start();

        ApplyArguments(startup, initialLaunch: true);

        _log.Information("Оболочка запущена через {StartupMs} мс: хоткей {HotkeyState}, аргументы {Arguments}",
            StartupTimer.Milliseconds,
            _activeHotkey?.Text ?? "не зарегистрирован",
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
    /// Завершает приложение: закрывает окна, снимает хоткей, трей и сервер экземпляра, останавливает цикл сообщений.
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
        _instanceServer?.Dispose();
        _instanceServer = null;

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

    /// <summary>
    /// Применяет аргументы: при первом запуске главное окно показывается, если не просили <c>--minimized</c>;
    /// от второго экземпляра без аргументов — просто выводит главное окно на передний план.
    /// </summary>
    private void ApplyArguments(StartupArguments startup, bool initialLaunch)
    {
        if (startup.Exit)
        {
            Exit();
            return;
        }

        var showMain = initialLaunch
            ? !startup.Minimized || startup.Show || startup.OpenSettings
            : startup.Show || startup.OpenSettings || (!startup.Minimized && !startup.ShowSearch);

        if (showMain)
        {
            ShowMainWindow();
        }

        if (startup.OpenSettings)
        {
            NavigateTo(PageKeys.Settings);
        }

        if (startup.ShowSearch)
        {
            _services.GetRequiredService<QuickSearchViewModel>().Query = startup.SearchQuery ?? string.Empty;
            ShowQuickSearch();
        }
    }

    private void OnSecondInstanceArguments(IReadOnlyList<string> args)
        => Dispatcher.UIThread.Post(() => ApplyArguments(StartupArguments.Parse(args), initialLaunch: false));

    private void CreateTray()
    {
        var menu = new NativeMenu();

        var open = new NativeMenuItem("Открыть DiskApp");
        open.Click += (_, _) => ShowMainWindow();
        menu.Items.Add(open);

        var search = new NativeMenuItem("Быстрый поиск") { Gesture = _activeHotkey?.Gesture };
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

    /// <summary>
    /// Регистрирует первое свободное сочетание из <see cref="QuickSearchHotkeys"/> и записывает итог в <see cref="HotkeyStatus"/>.
    /// </summary>
    private void RegisterHotkey()
    {
        var status = _services.GetRequiredService<HotkeyStatus>();

        if (!OperatingSystem.IsWindows())
        {
            status.IsRegistered = false;
            status.Message = "Глобальные горячие клавиши пока работают только в Windows.";
            return;
        }

        try
        {
            _messageWindow = new MessageWindow();
        }
        catch (Win32Exception exception)
        {
            status.IsRegistered = false;
            status.Message = $"Не удалось создать окно для приёма клавиш: {exception.Message}";
            _log.Warning(exception, "Окно сообщений для горячей клавиши не создано");
            return;
        }

        var candidates = QuickSearchHotkeys();
        var failures = new List<string>();
        foreach (var candidate in candidates)
        {
            try
            {
                _hotkey = GlobalHotkey.Register(_messageWindow, SearchHotkeyId, candidate.Modifiers, candidate.VirtualKey);
            }
            catch (Win32Exception exception)
            {
                failures.Add($"{candidate.Text} — {exception.Message}");
                _log.Debug("Сочетание {Hotkey} не зарегистрировано: {Reason}", candidate.Text, exception.Message);
                continue;
            }

            _hotkey.Pressed += (_, _) => ToggleQuickSearch();
            _activeHotkey = candidate;
            status.QuickSearchText = candidate.Text;
            status.IsRegistered = true;
            status.Message = ReferenceEquals(candidate, candidates[0])
                ? string.Empty
                : $"Сочетание {candidates[0].Text} занято другим приложением, поэтому действует {candidate.Text}.";

            if (status.Message.Length > 0)
            {
                _log.Warning("Запасное сочетание: {Message}", status.Message);
            }

            return;
        }

        status.IsRegistered = false;
        status.Message = "Ни одно сочетание не удалось зарегистрировать: " + string.Join("; ", failures) + ". Поиск открывается щелчком по иконке в трее.";
        _log.Warning("Горячая клавиша недоступна: {Message}", status.Message);
    }

    private void ToggleQuickSearch() => _quickSearch?.Toggle(CursorPosition());

    private MainWindow CreateMainWindow()
    {
        var viewModel = _services.GetRequiredService<MainViewModel>();
        var window = new MainWindow { DataContext = viewModel };
        _lifetime.MainWindow = window;

        window.Opened += (_, _) => _log.Information("Главное окно открыто через {StartupMs} мс после старта процесса", StartupTimer.Milliseconds);

        var hotkeys = _services.GetRequiredService<HotkeyStatus>();
        if (!hotkeys.IsRegistered)
        {
            viewModel.ShowNotice("Горячая клавиша недоступна", hotkeys.Message, FAInfoBarSeverity.Warning);
        }
        else if (hotkeys.Message.Length > 0)
        {
            viewModel.ShowNotice("Запасное сочетание клавиш", hotkeys.Message, FAInfoBarSeverity.Informational);
        }

        return window;
    }

    /// <summary>
    /// Сочетание клавиш: модификаторы и код для <c>RegisterHotKey</c>, подпись и жест для меню.
    /// </summary>
    private sealed record HotkeyCandidate(uint Modifiers, uint VirtualKey, string Text, KeyGesture Gesture);

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

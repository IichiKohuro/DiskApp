using System.ComponentModel;
using System.Runtime.Versioning;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

using DiskApp.Client;
using DiskApp.Contracts.Operations;
using DiskApp.Core.Abstractions;
using DiskApp.Core.Settings;
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
/// Оболочка десктопа: иконка в трее с меню и состоянием индексации, глобальная горячая клавиша из настроек,
/// приём аргументов от вторых экземпляров, главное окно (создаётся при первом обращении) и заранее созданное
/// окно быстрого поиска. Реализует <see cref="IAppCommands"/> для интерфейса.
/// </summary>
internal sealed class DesktopShell : IAppCommands, IDisposable
{
    private const int SearchHotkeyId = 1;
    private const string IconUri = "avares://DiskApp/Assets/DiskApp.ico";
    private static readonly TimeSpan OperationsCheckTimeout = TimeSpan.FromSeconds(2);

    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly IServiceProvider _services;
    private readonly InstanceNames _names;
    private readonly ILogger _log;
    private readonly TrayIconRenderer _icons = new(new Uri(IconUri));
    private readonly TrayStatus _trayStatus = new();

    private TrayIcon? _tray;
    private NativeMenuItem? _searchItem;
    private NativeMenuItem? _pauseItem;
    private MessageWindow? _messageWindow;
    private GlobalHotkey? _hotkey;
    private Hotkey? _activeHotkey;
    private string? _registeredSetting;
    private IDisposable? _settingsSubscription;
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

        // Иконки в шаблонах строк берутся из общего кэша: элементы шаблонов создаёт Avalonia, а не контейнер.
        DiskApp.UI.Controls.FileIcon.Cache = _services.GetService<IIconCache>();
        _quickSearch = new QuickSearchWindow { DataContext = _services.GetRequiredService<QuickSearchViewModel>() };
        ApplyMotion(_services.GetRequiredService<ISettingsStore<AppSettings>>().Current);

        RegisterHotkey();
        CreateTray();
        _log.Information("Трей показан через {StartupMs} мс после старта процесса", StartupTimer.Milliseconds);
        ConnectStatus();
        _services.GetRequiredService<OperationsViewModel>().PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OperationsViewModel.Summary))
            {
                UpdateTray();
            }
        };

        _instanceServer = new InstanceServer(_names.Pipe, OnSecondInstanceArguments, _log);
        _instanceServer.Start();

        ApplyArguments(startup, initialLaunch: true);

        _log.Information("Оболочка запущена через {StartupMs} мс: хоткей {HotkeyState}, аргументы {Arguments}",
            StartupTimer.Milliseconds,
            _activeHotkey?.DisplayText ?? "не зарегистрирован",
            startup);
    }

    /// <inheritdoc />
    public void ShowQuickSearch()
    {
        if (_quickSearch is null)
        {
            return;
        }

        // Индекс мог измениться, пока окно было скрыто: подборка и результаты перечитываются при каждом показе.
        _ = _services.GetRequiredService<QuickSearchViewModel>().ActivateAsync();
        _quickSearch.ShowAt(CursorPosition());
    }

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
    /// Выход из меню трея: если идут файловые операции и в настройках включено подтверждение, сначала спрашивает.
    /// </summary>
    public void Exit() => _ = ExitAsync();

    /// <summary>
    /// Главное окно для диалогов: показывается, если было скрыто в трее.
    /// </summary>
    public Window MainWindowForDialogs()
    {
        ShowMainWindow();
        return _mainWindow!;
    }

    /// <summary>
    /// Завершает приложение без вопросов: закрывает окна, снимает хоткей, трей и сервер экземпляра, останавливает цикл сообщений.
    /// </summary>
    public void ExitNow()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _log.Information("Выход по команде пользователя");

        if (_mainWindow is not null)
        {
            SavePlacement(_mainWindow);
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
        _settingsSubscription?.Dispose();
        _settingsSubscription = null;
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
            // --exit из скрипта или установщика завершает без вопросов.
            ExitNow();
            return;
        }

        if (startup.AddPaths is { Count: > 0 } addPaths)
        {
            _ = AddRootsAsync(addPaths);
        }

        var showMain = initialLaunch
            ? !startup.Minimized || startup.Show || startup.OpenSettings || startup.Page is not null
            : startup.Show || startup.OpenSettings || startup.Page is not null || (!startup.Minimized && !startup.ShowSearch);

        if (showMain)
        {
            ShowMainWindow();
        }

        if (startup.OpenSettings)
        {
            NavigateTo(PageKeys.Settings);
        }

        if (startup.Page is { } page)
        {
            NavigateTo(page);
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

        _searchItem = new NativeMenuItem("Быстрый поиск") { Gesture = _activeHotkey?.ToGesture() };
        _searchItem.Click += (_, _) => ShowQuickSearch();
        menu.Items.Add(_searchItem);

        menu.Items.Add(new NativeMenuItemSeparator());

        _pauseItem = new NativeMenuItem("Приостановить индексацию") { IsEnabled = false };
        _pauseItem.Click += (_, _) => _ = TogglePauseAsync();
        menu.Items.Add(_pauseItem);

        var settings = new NativeMenuItem("Настройки");
        settings.Click += (_, _) => NavigateTo(PageKeys.Settings);
        menu.Items.Add(settings);

        menu.Items.Add(new NativeMenuItemSeparator());

        var exit = new NativeMenuItem("Выход");
        exit.Click += (_, _) => Exit();
        menu.Items.Add(exit);

        _tray = new TrayIcon
        {
            Icon = IconFor(_trayStatus.State),
            ToolTipText = _trayStatus.ToolTip,
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
    /// Регистрирует сочетание быстрого поиска из настроек, а если оно занято — первое свободное из запасных (ADR-0006);
    /// итог — в <see cref="HotkeyStatus"/>. Изменение сочетания в настройках перерегистрирует его без перезапуска.
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

        var settings = _services.GetRequiredService<ISettingsStore<AppSettings>>();
        RegisterQuickSearchHotkey(settings.Current.Hotkeys.QuickSearch);
        _settingsSubscription = settings.Subscribe(changed => Dispatcher.UIThread.Post(() =>
        {
            ApplyMotion(changed);
            if (OperatingSystem.IsWindows() && !_exiting && !string.Equals(changed.Hotkeys.QuickSearch, _registeredSetting, StringComparison.Ordinal))
            {
                RegisterQuickSearchHotkey(changed.Hotkeys.QuickSearch);
            }
        }));
    }

    [SupportedOSPlatform("windows")]
    private void RegisterQuickSearchHotkey(string configured)
    {
        var status = _services.GetRequiredService<HotkeyStatus>();
        _hotkey?.Dispose();
        _hotkey = null;
        _activeHotkey = null;
        _registeredSetting = configured;

        var recognized = Hotkey.TryParse(configured, out var preferred);
        preferred ??= Hotkey.DefaultQuickSearch;
        var candidates = new[] { preferred }.Concat(Hotkey.QuickSearchFallbacks).Distinct().ToArray();
        var failures = new List<string>();
        foreach (var candidate in candidates)
        {
            if (HotkeyKeys.ToVirtualKey(candidate.Key) is not { } virtualKey)
            {
                failures.Add($"{candidate.DisplayText} — клавиша не поддерживается");
                continue;
            }

            try
            {
                _hotkey = GlobalHotkey.Register(_messageWindow!, SearchHotkeyId, (uint)candidate.Modifiers, virtualKey);
            }
            catch (Win32Exception exception)
            {
                failures.Add($"{candidate.DisplayText} — {exception.Message}");
                _log.Debug("Сочетание {Hotkey} не зарегистрировано: {Reason}", candidate.DisplayText, exception.Message);
                continue;
            }

            _hotkey.Pressed += (_, _) => ToggleQuickSearch();
            _activeHotkey = candidate;
            status.QuickSearchText = candidate.DisplayText;
            status.IsRegistered = true;
            status.Message = !recognized
                ? $"Сочетание «{configured}» в настройках не распознано, поэтому действует {candidate.DisplayText}."
                : candidate == preferred
                    ? string.Empty
                    : $"Сочетание {preferred.DisplayText} занято другим приложением, поэтому действует {candidate.DisplayText}. Его можно сменить в настройках.";

            if (status.Message.Length > 0)
            {
                _log.Warning("Запасное сочетание: {Message}", status.Message);
            }

            if (_searchItem is not null)
            {
                _searchItem.Gesture = candidate.ToGesture();
            }

            return;
        }

        status.IsRegistered = false;
        status.Message = "Ни одно сочетание не удалось зарегистрировать: " + string.Join("; ", failures)
            + ". Поиск открывается щелчком по иконке в трее; сочетание можно сменить в настройках.";
        _log.Warning("Горячая клавиша недоступна: {Message}", status.Message);
    }

    /// <summary>
    /// Подписывается на события хаба: подсказка, значок и пункт паузы в трее следят за индексацией.
    /// Подключение ждёт, пока сервер внутри процесса не запустится.
    /// </summary>
    private void ConnectStatus()
    {
        var hub = _services.GetRequiredService<DiskAppHubConnection>();
        hub.StatusReceived += status => Dispatcher.UIThread.Post(() =>
        {
            _trayStatus.Apply(status);
            UpdateTray();
        });
        hub.RootStatusChanged += change => Dispatcher.UIThread.Post(() =>
        {
            _trayStatus.Apply(change);
            UpdateTray();
        });
        hub.IndexProgress += progress => Dispatcher.UIThread.Post(() =>
        {
            _trayStatus.Apply(progress);
            UpdateTray();
        });
        _ = StartHubAsync(hub);
    }

    // Главное окно показывает то же состояние, что и трей: точка в панели навигации и подпись под ней.
    private void UpdateMainStatus()
    {
        var health = _trayStatus.State switch
        {
            TrayState.Starting => IndexHealth.Starting,
            TrayState.Idle => IndexHealth.Idle,
            TrayState.Indexing => IndexHealth.Indexing,
            TrayState.Paused => IndexHealth.Paused,
            TrayState.Offline => IndexHealth.Offline,
            _ => IndexHealth.Error,
        };
        const string prefix = "DiskApp — ";
        var text = _trayStatus.ToolTip;
        text = text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : text;
        text = text.Length > 0 ? char.ToUpperInvariant(text[0]) + text[1..] : text;
        _services.GetRequiredService<MainViewModel>().ApplyIndexHealth(health, text, text);
    }

    private async Task StartHubAsync(DiskAppHubConnection hub)
    {
        try
        {
            await hub.StartAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log.Warning(exception, "Хаб событий недоступен");
            Dispatcher.UIThread.Post(() =>
            {
                _trayStatus.Fail(exception.Message);
                UpdateTray();
            });
        }
    }

    private void UpdateTray()
    {
        UpdateMainStatus();
        if (_tray is null)
        {
            return;
        }

        // Идущие файловые операции дописываются к состоянию индекса; Windows обрезает подсказку до 127 символов.
        var tooltip = _trayStatus.ToolTip;
        if (_services.GetService<OperationsViewModel>() is { HasActive: true } operations)
        {
            tooltip += " · " + operations.Summary;
        }

        _tray.ToolTipText = tooltip.Length <= 127 ? tooltip : tooltip[..126] + "…";
        _tray.Icon = IconFor(_trayStatus.State) ?? _tray.Icon;
        if (_pauseItem is not null)
        {
            _pauseItem.Header = _trayStatus.IsPaused ? "Возобновить индексацию" : "Приостановить индексацию";
            _pauseItem.IsEnabled = _trayStatus.State is not (TrayState.Starting or TrayState.Error) || _trayStatus.IsPaused;
        }
    }

    private async Task TogglePauseAsync()
    {
        var api = _services.GetRequiredService<IDiskAppApi>();
        try
        {
            var status = _trayStatus.IsPaused
                ? await api.ResumeIndexingAsync().ConfigureAwait(false)
                : await api.PauseIndexingAsync().ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                _trayStatus.Apply(status);
                UpdateTray();
            });
        }
        catch (Exception exception) when (exception is DiskAppApiException or HttpRequestException or OperationCanceledException)
        {
            _log.Warning(exception, "Пауза индексации не переключилась");
            Dispatcher.UIThread.Post(() => _services.GetRequiredService<IInfoBarService>()
                .Show("Пауза индексации не переключилась", exception.Message, FAInfoBarSeverity.Error));
        }
    }

    /// <summary>
    /// <c>--add &lt;пути&gt;</c>: добавляет папки корнями через API (запрос ждёт старта сервера) и показывает итог в главном окне.
    /// </summary>
    private async Task AddRootsAsync(IReadOnlyList<string> paths)
    {
        var api = _services.GetRequiredService<IDiskAppApi>();
        var added = new List<string>();
        var failed = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                var root = await api.CreateRootAsync(new Contracts.Roots.CreateRootRequest { Path = path }).ConfigureAwait(false);
                added.Add(root.Label);
            }
            catch (Exception exception) when (exception is DiskAppApiException or HttpRequestException or InvalidOperationException)
            {
                failed.Add($"{path}: {exception.Message}");
                _log.Warning(exception, "Корень {Path} не добавлен", path);
            }
        }

        Dispatcher.UIThread.Post(() =>
        {
            ShowMainWindow();
            var notices = _services.GetRequiredService<IInfoBarService>();
            if (failed.Count == 0)
            {
                notices.Show("Корни добавлены", string.Join(", ", added) + ". Индексация уже идёт.", FAInfoBarSeverity.Success);
            }
            else
            {
                var message = (added.Count > 0 ? "Добавлены: " + string.Join(", ", added) + ". " : string.Empty) + "Не добавлены: " + string.Join("; ", failed);
                notices.Show("Не все корни добавлены", message, FAInfoBarSeverity.Warning);
            }
        });
    }

    private async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        var settings = _services.GetRequiredService<ISettingsStore<AppSettings>>().Current;
        if (settings.Tray.ConfirmExitDuringOperations && await CountActiveOperationsAsync().ConfigureAwait(true) is > 0 and var active)
        {
            var confirmed = await _services.GetRequiredService<IDialogService>().ConfirmAsync(
                "Выйти из DiskApp?",
                $"Идут файловые операции: {active}. При выходе они отменятся, недокопированные файлы удалятся.",
                "Выйти").ConfigureAwait(true);
            if (!confirmed)
            {
                return;
            }
        }

        ExitNow();
    }

    private async Task<int> CountActiveOperationsAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(OperationsCheckTimeout);
            var operations = await _services.GetRequiredService<IDiskAppApi>().GetOperationsAsync(timeout.Token).ConfigureAwait(true);
            return operations.Count(static operation => operation.State is OperationState.Queued or OperationState.Running or OperationState.WaitingDecision);
        }
        catch (Exception exception) when (exception is DiskAppApiException or HttpRequestException or OperationCanceledException)
        {
            // Сервер недоступен — и операций, которые можно прервать, нет.
            return 0;
        }
    }

    private void ToggleQuickSearch() => _quickSearch?.Toggle(CursorPosition());

    private MainWindow CreateMainWindow()
    {
        var viewModel = _services.GetRequiredService<MainViewModel>();
        var window = new MainWindow { DataContext = viewModel };
        _lifetime.MainWindow = window;
        RestorePlacement(window);

        window.Opened += (_, _) => _log.Information("Главное окно открыто через {StartupMs} мс после старта процесса", StartupTimer.Milliseconds);
        window.Closing += (_, _) => SavePlacement(window);
        var settings = _services.GetRequiredService<ISettingsStore<AppSettings>>();
        window.CloseToTray = () => settings.Current.Tray.CloseToTray;
        window.ExitRequested += (_, _) => Exit();

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
    /// «Уменьшить движение»: настройка приложения или выключенные эффекты анимации Windows.
    /// </summary>
    private void ApplyMotion(AppSettings settings)
    {
        var reduce = settings.Appearance.ReduceMotion || (OperatingSystem.IsWindows() && !NativeMethods.AnimationsEnabled());
        if (_quickSearch is not null)
        {
            _quickSearch.ReduceMotion = reduce;
        }

        _services.GetRequiredService<MainViewModel>().ReduceMotion = reduce;
    }

    private WindowPlacementStore Placements()
        => new(Path.Combine(_services.GetRequiredService<IAppPaths>().DataDirectory, "window-state.json"));

    /// <summary>
    /// Положение и размер главного окна с прошлого раза; если заголовок не попадает ни на один экран — по центру.
    /// </summary>
    private void RestorePlacement(MainWindow window)
    {
        if (Placements().LoadMain() is not { } placement)
        {
            return;
        }

        window.Width = Math.Max(window.MinWidth, placement.Width);
        window.Height = Math.Max(window.MinHeight, placement.Height);
        if (WindowPlacementStore.IsReachable(placement, window.Screens.All.Select(static screen => screen.WorkingArea)))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(placement.X, placement.Y);
        }

        if (placement.Maximized)
        {
            window.WindowState = WindowState.Maximized;
        }
    }

    private void SavePlacement(MainWindow window)
    {
        if (!window.IsVisible && window.Position == default)
        {
            return;
        }

        // У развёрнутого окна сохраняется прежний обычный размер, иначе после «восстановить» оно останется во весь экран.
        var maximized = window.WindowState == WindowState.Maximized;
        var previous = Placements().LoadMain();
        var placement = maximized && previous is not null
            ? previous with { Maximized = true }
            : new WindowPlacement(window.Position.X, window.Position.Y, window.Width, window.Height, maximized);
        Placements().SaveMain(placement);
    }

    private WindowIcon? IconFor(TrayState state)
    {
        try
        {
            return _icons.For(state);
        }
        catch (Exception exception)
        {
            _log.Warning(exception, "Иконка трея не построена из {Uri}", IconUri);
            return null;
        }
    }
}

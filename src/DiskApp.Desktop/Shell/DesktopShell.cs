using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

using DiskApp.Client;
using DiskApp.Contracts.Operations;
using DiskApp.Core.Abstractions;
using DiskApp.Core.Settings;
using DiskApp.Desktop.Platform.Windows;
using DiskApp.Desktop.SingleInstance;
using DiskApp.Desktop.Startup;
using DiskApp.Desktop.Windows;
using DiskApp.UI.Localization;
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
internal sealed class DesktopShell : IAppCommands, IHotkeyChecker, IDisposable
{
    private const int SearchHotkeyId = 1;
    private const int ProbeHotkeyId = 99;
    private const string IconUri = "avares://DiskApp/Assets/DiskApp.ico";
    private static readonly TimeSpan OperationsCheckTimeout = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan ImportBatchWindow = TimeSpan.FromMilliseconds(400);

    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly List<string> _pendingImport = [];
    private readonly Lock _importGate = new();
    private readonly IServiceProvider _services;
    private readonly InstanceNames _names;
    private readonly ILogger _log;
    private readonly TrayIconRenderer _icons = new(new Uri(IconUri));
    private readonly TrayStatus _trayStatus = new();

    private TrayIcon? _tray;
    private NativeMenuItem? _searchItem;
    private NativeMenuItem? _pauseItem;
    private NativeMenuItem? _openItem;
    private NativeMenuItem? _settingsItem;
    private NativeMenuItem? _exitItem;
    private MessageWindow? _messageWindow;
    private GlobalHotkey? _hotkey;
    private Hotkey? _activeHotkey;
    private string? _registeredSetting;
    private IDisposable? _settingsSubscription;
    private InstanceServer? _instanceServer;
    private MainWindow? _mainWindow;
    private QuickSearchWindow? _quickSearch;
    private bool _exiting;
    private bool _whatsNewPending;
    private bool _safeMode;
    private ErrorWindow? _errorWindow;
    private ErrorReportViewModel? _errorReport;

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

        // Язык — до создания окон и трея: строки, собранные в коде, берутся на нём (этап 9.2).
        DiskApp.UI.Localization.Localizer.Instance.SetLanguage(_services.GetRequiredService<ISettingsStore<AppSettings>>().Current.Appearance.Language);

        var startup = StartupArguments.Parse(args);
        _safeMode = startup.SafeMode;

        // Иконки в шаблонах строк берутся из общего кэша: элементы шаблонов создаёт Avalonia, а не контейнер.
        DiskApp.UI.Controls.FileIcon.Cache = _services.GetService<IIconCache>();
        _quickSearch = new QuickSearchWindow { DataContext = _services.GetRequiredService<QuickSearchViewModel>() };
        ApplySettings(_services.GetRequiredService<ISettingsStore<AppSettings>>().Current);

        RegisterHotkey();
        CreateTray();
        DiskApp.UI.Localization.Localizer.Instance.LanguageChanged += (_, _) => Dispatcher.UIThread.Post(RelabelTray);
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

        // Окно поиска прогревается, когда старт уже позади: на критическом пути трея его нет (этап 10.2).
        DispatcherTimer.RunOnce(() => _quickSearch?.Prewarm(), TimeSpan.FromSeconds(3));

        ApplyArguments(startup, initialLaunch: true);
        if (!startup.Exit)
        {
            if (_safeMode)
            {
                ShowSafeModeNotice();
            }
            else
            {
                _ = StartOnboardingAsync();
            }

            if (CrashHandler.TakeMarker(_services.GetRequiredService<IAppPaths>().DataDirectory) is { } crash)
            {
                _log.Warning("Прошлый сеанс завершился аварийно");
                Dispatcher.UIThread.Post(() => ShowErrorReport(crash, previousCrash: true), DispatcherPriority.Background);
            }
        }

        _log.Information("Оболочка запущена через {StartupMs} мс: хоткей {HotkeyState}, аргументы {Arguments}",
            StartupTimer.Milliseconds,
            _activeHotkey?.DisplayText ?? "не зарегистрирован",
            startup);
    }

    /// <inheritdoc />
    public void ShowQuickSearch()
    {
        LeaveFolderPick();
        ShowQuickSearchWindow();
    }

    private void ShowQuickSearchWindow()
    {
        if (_quickSearch is null)
        {
            return;
        }

        // Индекс мог измениться, пока окно было скрыто: подборка и результаты перечитываются при каждом показе.
        _ = _services.GetRequiredService<QuickSearchViewModel>().ActivateAsync();
        _quickSearch.ShowAt(PopupAnchor());
    }

    /// <inheritdoc />
    public string? Check(Hotkey hotkey, HotkeyTarget target)
    {
        ArgumentNullException.ThrowIfNull(hotkey);

        if (!OperatingSystem.IsWindows() || _messageWindow is null)
        {
            return L.T("DesktopShell.GlobalShortcutsWorkOnlyOnWindows");
        }

        var quickSwitch = _services.GetService<QuickSwitchController>();
        var current = target == HotkeyTarget.QuickSearch ? _activeHotkey : quickSwitch?.RegisteredHotkey;
        var other = target == HotkeyTarget.QuickSearch ? quickSwitch?.RegisteredHotkey : _activeHotkey;
        if (hotkey == current)
        {
            return null;
        }

        if (hotkey == other)
        {
            return target == HotkeyTarget.QuickSearch ? L.T("DesktopShell.ThisShortcutAlreadyBelongsToQuick") : L.T("DesktopShell.ThisShortcutAlreadyBelongsToQuick2");
        }

        if (HotkeyKeys.ToVirtualKey(hotkey.Key) is not { } virtualKey)
        {
            return L.T("DesktopShell.TheKeyIsNotSupported");
        }

        // Пробная регистрация: если Windows отдала сочетание, оно свободно; сразу освобождаем его.
        if (!NativeMethods.RegisterHotKey(_messageWindow.Handle, ProbeHotkeyId, (uint)hotkey.Modifiers | NativeMethods.ModNoRepeat, virtualKey))
        {
            return L.T("DesktopShell.TheShortcutIsTakenByAnother");
        }

        NativeMethods.UnregisterHotKey(_messageWindow.Handle, ProbeHotkeyId);
        return null;
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

        if (_whatsNewPending)
        {
            _whatsNewPending = false;
            Dispatcher.UIThread.Post(() => _ = ShowWhatsNewAsync(), DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// Мастер первого запуска и «Что нового» (PLAN.md, этап 9.3). Мастер открывается, пока он не пройден и в индексе
    /// нет ни одной папки; если папки уже есть (обновление с версии без мастера), он считается пройденным. После
    /// обновления версии при следующем показе главного окна открывается «Что нового»; обучающие подсказки включаются
    /// после мастера или после «Что нового», чтобы не перекрывать их.
    /// </summary>
    private async Task StartOnboardingAsync()
    {
        try
        {
            var onboarding = _services.GetRequiredService<ISettingsStore<AppSettings>>().Current.Onboarding;
            if (!onboarding.Completed)
            {
                var roots = await _services.GetRequiredService<IRootsService>().GetRootsAsync().ConfigureAwait(false);
                if (roots.Count == 0)
                {
                    await Dispatcher.UIThread.InvokeAsync(ShowOnboardingAsync);
                    return;
                }

                await _services.GetRequiredService<ISettingsService>()
                    .PatchAsync(new JsonObject { ["onboarding"] = new JsonObject { ["completed"] = true } })
                    .ConfigureAwait(false);
            }

            if (onboarding.LastSeenVersion != AppVersion.Current)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _whatsNewPending = true;
                    if (_mainWindow is { IsVisible: true })
                    {
                        ShowMainWindow();
                    }
                });
                return;
            }

            await EnableTipsAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log.Warning(exception, "Не удалось проверить, пройден ли мастер первого запуска");
        }
    }

    /// <summary>
    /// Окно «Что-то пошло не так» для необработанной ошибки (этап 9.5); пока оно открыто, новые ошибки только увеличивают
    /// счётчик в нём.
    /// </summary>
    public void ReportError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ShowErrorReport(exception.ToString(), previousCrash: false);
    }

    private void ShowErrorReport(string details, bool previousCrash)
    {
        if (_errorReport is { } open && _errorWindow is { } shown)
        {
            open.AddError(details);
            shown.Activate();
            return;
        }

        var report = new ErrorReportViewModel(_services.GetRequiredService<IAppMaintenance>(), details, previousCrash);
        var window = new ErrorWindow(report);
        window.Closed += (_, _) =>
        {
            _errorWindow = null;
            _errorReport = null;
        };
        _errorReport = report;
        _errorWindow = window;
        window.Show();
        window.Activate();
    }

    // Безопасный режим (этап 9.5): полоса предупреждения с действием «Сбросить настройки».
    private void ShowSafeModeNotice() => _services.GetRequiredService<IInfoBarService>().Show(
        L.T("SafeMode.Title"),
        L.T("SafeMode.Message"),
        FAInfoBarSeverity.Warning,
        L.T("SafeMode.Reset"),
        () => _ = ResetSettingsAsync());

    private async Task ResetSettingsAsync()
    {
        try
        {
            var confirmed = await _services.GetRequiredService<IDialogService>().ConfirmAsync(
                L.T("SafeMode.ResetTitle"), L.T("SafeMode.ResetMessage"), L.T("SafeMode.Reset"));
            if (!confirmed)
            {
                return;
            }

            var backup = await _services.GetRequiredService<IAppMaintenance>().ResetSettingsAsync();
            _services.GetRequiredService<IInfoBarService>().Show(
                L.T("SafeMode.ResetDone"),
                backup is null ? string.Empty : L.F("SafeMode.BackupAt", backup),
                FAInfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log.Error(exception, "Настройки не сброшены");
            _services.GetRequiredService<IInfoBarService>().Show(L.T("SafeMode.ResetFailed"), exception.Message, FAInfoBarSeverity.Error);
        }
    }

    /// <inheritdoc />
    public void StartOnboarding()
    {
        if (_services.GetRequiredService<MainViewModel>().IsOnboarding)
        {
            ShowMainWindow();
            return;
        }

        _services.GetService<SettingsTipsService>()?.Reset();
        _ = ShowOnboardingSafeAsync();
    }

    private async Task ShowOnboardingSafeAsync()
    {
        try
        {
            await ShowOnboardingAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log.Warning(exception, "Мастер первого запуска не открылся");
        }
    }

    private async Task ShowOnboardingAsync()
    {
        var wizard = _services.GetRequiredService<OnboardingViewModel>();
        await wizard.LoadAsync();
        _services.GetRequiredService<MainViewModel>().StartOnboarding(wizard);

        // После обработчика главного окна (оно уже перешло на главную): включить подсказки и показать первую.
        wizard.Completed += (_, _) =>
        {
            _services.GetService<SettingsTipsService>()?.Enable();
            ShowHomeTips();
        };
        ShowMainWindow();
        _log.Information("Открыт мастер первого запуска");
    }

    private async Task ShowWhatsNewAsync()
    {
        try
        {
            await _services.GetRequiredService<IDialogService>().ShowWhatsNewAsync(new WhatsNewViewModel());

            // Версия запоминается после показа: если окно не открылось, «Что нового» покажется в следующий раз.
            await _services.GetRequiredService<ISettingsService>()
                .PatchAsync(new JsonObject { ["onboarding"] = new JsonObject { ["lastSeenVersion"] = AppVersion.Current } });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log.Warning(exception, "Окно «Что нового» не показано");
        }

        await EnableTipsAsync();
    }

    private async Task EnableTipsAsync()
    {
        if (_services.GetService<SettingsTipsService>() is not { } tips)
        {
            return;
        }

        try
        {
            await tips.LoadAsync().ConfigureAwait(false);
            ShowHomeTips();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log.Warning(exception, "Состояние обучающих подсказок не прочитано");
        }
    }

    private void ShowHomeTips() => Dispatcher.UIThread.Post(
        () =>
        {
            var main = _services.GetRequiredService<MainViewModel>();
            if (_mainWindow is { IsVisible: true, IsActive: true } && main.SelectedPage is HomeViewModel home && !main.IsOnboarding)
            {
                home.ShowTips();
            }
        },
        DispatcherPriority.Background);

    // Проводник запускает пункт меню отдельно для каждого выбранного файла: запросы, пришедшие за 400 мс, —
    // одно окно «Добавить».
    private void QueueImport(IReadOnlyList<string> paths)
    {
        lock (_importGate)
        {
            _pendingImport.AddRange(paths);
            if (_pendingImport.Count != paths.Count)
            {
                return;
            }
        }

        _ = FlushImportAsync();
    }

    private async Task FlushImportAsync()
    {
        await Task.Delay(ImportBatchWindow).ConfigureAwait(true);
        string[] paths;
        lock (_importGate)
        {
            paths = [.. _pendingImport.Distinct(StringComparer.OrdinalIgnoreCase)];
            _pendingImport.Clear();
        }

        await AddFilesAsync(paths).ConfigureAwait(true);
    }

    /// <inheritdoc />
    public Task AddFilesAsync(IReadOnlyList<string> paths)
    {
        ShowMainWindow();
        return _services.GetRequiredService<IAddFilesService>().AddAsync(paths);
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

            // Quick Switch освобождает контейнер, и раньше оболочки: она создана первой. Обращаться к контейнеру здесь
            // нельзя — при остановке хоста он уже освобождён, и исключение роняло процесс на выходе (найдено в 9.5).
            _messageWindow?.Dispose();
            _messageWindow = null;
        }

        _tray?.Dispose();
        _tray = null;
    }

    /// <summary>
    /// Точка, по которой выбирается монитор окна поиска (настройка «Монитор», этап 9.1): курсор, центр активного окна или
    /// центр основного монитора.
    /// </summary>
    private PixelPoint? PopupAnchor()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return _services.GetRequiredService<ISettingsStore<AppSettings>>().Current.Popup.Monitor switch
        {
            PopupMonitor.ActiveWindow => WindowEffects.GetForegroundWindowCenter() ?? WindowEffects.GetCursorPosition(),
            PopupMonitor.Primary => _quickSearch?.Screens.Primary?.Bounds.Center ?? WindowEffects.GetCursorPosition(),
            _ => WindowEffects.GetCursorPosition(),
        };
    }

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

        // --minimized приходит от автозапуска; настройка «Запускаться свёрнутым» решает, оставаться ли только в трее.
        var minimized = startup.Minimized && _services.GetRequiredService<ISettingsStore<AppSettings>>().Current.Tray.StartMinimized;
        var showMain = initialLaunch
            ? !minimized || startup.Show || startup.OpenSettings || startup.Page is not null
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

        if (startup.Import is { Count: > 0 } import)
        {
            QueueImport(import);
        }

        if (startup.BrowsePath is { Length: > 0 } browsePath)
        {
            NavigateTo(PageKeys.Browse);
            _ = _services.GetRequiredService<BrowseViewModel>().NavigateAsync(browsePath);
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

        _openItem = new NativeMenuItem(L.T("DesktopShell.OpenDiskApp"));
        _openItem.Click += (_, _) => ShowMainWindow();
        menu.Items.Add(_openItem);

        _searchItem = new NativeMenuItem(L.T("DesktopShell.QuickSearch")) { Gesture = _activeHotkey?.ToGesture() };
        _searchItem.Click += (_, _) => ShowQuickSearch();
        menu.Items.Add(_searchItem);

        menu.Items.Add(new NativeMenuItemSeparator());

        _pauseItem = new NativeMenuItem(L.T("DesktopShell.PauseIndexing")) { IsEnabled = false };
        _pauseItem.Click += (_, _) => _ = TogglePauseAsync();
        menu.Items.Add(_pauseItem);

        _settingsItem = new NativeMenuItem(L.T("DesktopShell.Settings"));
        _settingsItem.Click += (_, _) => NavigateTo(PageKeys.Settings);
        menu.Items.Add(_settingsItem);

        menu.Items.Add(new NativeMenuItemSeparator());

        _exitItem = new NativeMenuItem(L.T("DesktopShell.Exit"));
        _exitItem.Click += (_, _) => Exit();
        menu.Items.Add(_exitItem);

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

    /// <summary>Подписи меню трея и подсказка — на новом языке после его смены (этап 9.2).</summary>
    private void RelabelTray()
    {
        if (_openItem is null || _searchItem is null || _settingsItem is null || _exitItem is null)
        {
            return;
        }

        _openItem.Header = L.T("DesktopShell.OpenDiskApp");
        _searchItem.Header = L.T("DesktopShell.QuickSearch");
        _settingsItem.Header = L.T("DesktopShell.Settings");
        _exitItem.Header = L.T("DesktopShell.Exit");
        UpdateTray();
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
            status.Message = L.T("DesktopShell.GlobalHotkeysWorkOnlyOnWindows");
            return;
        }

        try
        {
            _messageWindow = new MessageWindow();
        }
        catch (Win32Exception exception)
        {
            status.IsRegistered = false;
            status.Message = L.F("DesktopShell.CouldNotCreateTheWindowThat", exception.Message);
            _log.Warning(exception, "Окно сообщений для горячей клавиши не создано");
            return;
        }

        var settings = _services.GetRequiredService<ISettingsStore<AppSettings>>();
        RegisterQuickSearchHotkey(settings.Current.Hotkeys.QuickSearch);
        if (!_safeMode)
        {
            _services.GetService<QuickSwitchController>()?.Attach(_messageWindow, OnQuickSwitchPressed);
        }
        _settingsSubscription = settings.Subscribe(changed => Dispatcher.UIThread.Post(() =>
        {
            ApplySettings(changed);
            _ = _services.GetRequiredService<SettingsViewModel>().LoadSettingsAsync();
            if (OperatingSystem.IsWindows() && !_safeMode)
            {
                _services.GetService<QuickSwitchController>()?.Reconfigure();
            }

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
                failures.Add(L.F("DesktopShell.TheKeyIsNotSupported2", candidate.DisplayText));
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
                ? L.F("DesktopShell.TheShortcutInSettingsIsNot", configured, candidate.DisplayText)
                : candidate == preferred
                    ? string.Empty
                    : L.F("DesktopShell.IsTakenByAnotherAppSo", preferred.DisplayText, candidate.DisplayText);

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
        status.Message = L.T("DesktopShell.NoShortcutCouldBeRegistered") + string.Join("; ", failures)
            + L.T("DesktopShell.SearchOpensWithAClickOn");
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
            _pauseItem.Header = _trayStatus.IsPaused ? L.T("DesktopShell.ResumeIndexing") : L.T("DesktopShell.PauseIndexing");
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
                .Show(L.T("DesktopShell.IndexingPauseDidNotSwitch"), exception.Message, FAInfoBarSeverity.Error));
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
                notices.Show(L.T("DesktopShell.RootsAdded"), string.Join(", ", added) + L.T("DesktopShell.IndexingIsAlreadyRunning"), FAInfoBarSeverity.Success);
            }
            else
            {
                var message = (added.Count > 0 ? L.T("DesktopShell.Added") + string.Join(", ", added) + ". " : string.Empty) + L.T("DesktopShell.NotAdded") + string.Join("; ", failed);
                notices.Show(L.T("DesktopShell.NotAllRootsWereAdded"), message, FAInfoBarSeverity.Warning);
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
                L.T("DesktopShell.ExitDiskApp"),
                L.F("DesktopShell.FileOperationsAreRunningOnExit", active),
                L.T("DesktopShell.Exit2")).ConfigureAwait(true);
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

    private void ToggleQuickSearch()
    {
        LeaveFolderPick();
        _quickSearch?.Toggle(PopupAnchor());
    }

    /// <summary>
    /// Обычный показ поиска отменяет незавершённый выбор папки для диалога: окно могли закрыть щелчком мимо.
    /// </summary>
    private void LeaveFolderPick()
    {
        var viewModel = _services.GetRequiredService<QuickSearchViewModel>();
        if (viewModel.IsPickingFolder)
        {
            viewModel.CancelFolderPick();
            viewModel.Query = string.Empty;
        }
    }

    /// <summary>
    /// Quick Switch (PLAN.md, этап 8.6, ADR-0008): в стандартном диалоге «Сохранить» или «Открыть» — выбор папки в
    /// быстром поиске, затем переход диалога в неё; без диалога — подсказка.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private void OnQuickSwitchPressed()
    {
        var viewModel = _services.GetRequiredService<QuickSearchViewModel>();
        if (FileDialogBridge.FromForeground() is not { } dialog)
        {
            _log.Information("Quick Switch: на переднем плане нет стандартного диалога файлов");
            ShowQuickSearch();
            viewModel.ReportFolderPickProblem(
                L.T("DesktopShell.SaveDialogNotFound"),
                L.F("DesktopShell.OpenSaveAsOrOpenIn", _services.GetRequiredService<QuickSwitchController>().HotkeyText));
            return;
        }

        viewModel.BeginFolderPick(folder => NavigateDialogAsync(dialog, folder));
        ShowQuickSearchWindow();
    }

    [SupportedOSPlatform("windows")]
    private async Task NavigateDialogAsync(FileDialogTarget dialog, string folder)
    {
        var result = await FileDialogBridge.NavigateAsync(dialog, folder).ConfigureAwait(true);
        _log.Information("Quick Switch: {Result}", result);
        if (result == QuickSwitchResult.Navigated)
        {
            return;
        }

        var (title, message) = result switch
        {
            QuickSwitchResult.DialogClosed => (L.T("DesktopShell.TheDialogIsAlreadyClosed"), L.T("DesktopShell.TheSaveOrOpenWindowClosed")),
            QuickSwitchResult.FolderMissing => (L.T("DesktopShell.TheFolderIsGone"), L.T("DesktopShell.TheChosenFolderWasDeletedOr")),
            _ => (L.T("DesktopShell.TheDialogDidNotGoTo"), L.T("DesktopShell.ThePathIsCopiedToThe")),
        };

        if (result == QuickSwitchResult.Failed)
        {
            await _services.GetRequiredService<IClipboardService>().SetTextAsync(folder).ConfigureAwait(true);
        }

        ShowQuickSearch();
        _services.GetRequiredService<QuickSearchViewModel>().ReportFolderPickProblem(title, message);
    }

    private async Task AddFromClipboardAsync(Window window)
    {
        if (window.Clipboard is not { } clipboard)
        {
            return;
        }

        var add = _services.GetRequiredService<IAddFilesService>();
        try
        {
            if (await clipboard.TryGetFilesAsync() is { Length: > 0 } files)
            {
                await add.AddAsync([.. files.Select(static file => file.TryGetLocalPath()).OfType<string>()]);
            }
            else if (await clipboard.TryGetBitmapAsync() is { } bitmap)
            {
                await add.AddImageAsync(stream =>
                {
                    bitmap.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                    return Task.CompletedTask;
                });
            }
            else
            {
                await add.AddTextAsync(await clipboard.TryGetTextAsync() ?? string.Empty);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log.Warning(exception, "Буфер обмена не сохранился в файл");
            _services.GetRequiredService<IInfoBarService>().Show(L.T("DesktopShell.CouldNotSaveTheClipboard"), exception.Message, FAInfoBarSeverity.Error);
        }
    }

    private MainWindow CreateMainWindow()
    {
        var viewModel = _services.GetRequiredService<MainViewModel>();
        var window = new MainWindow { DataContext = viewModel };
        _lifetime.MainWindow = window;
        RestorePlacement(window);
        var appearance = _services.GetRequiredService<ISettingsStore<AppSettings>>().Current.Appearance;
        window.ApplyAppearance(EffectiveBackdrop(appearance), Math.Clamp(appearance.FontScale, 0.9, 1.5), appearance.Compact);

        window.Opened += (_, _) => _log.Information("Главное окно открыто через {StartupMs} мс после старта процесса", StartupTimer.Milliseconds);
        window.Closing += (_, _) => SavePlacement(window);
        var settings = _services.GetRequiredService<ISettingsStore<AppSettings>>();
        window.CloseToTray = () => settings.Current.Tray.CloseToTray;
        window.ExitRequested += (_, _) => Exit();

        // Ctrl+Shift+V — содержимое буфера обмена в файл (PLAN.md, этап 8.5).
        window.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.V && e.KeyModifiers == (Avalonia.Input.KeyModifiers.Control | Avalonia.Input.KeyModifiers.Shift))
            {
                e.Handled = true;
                _ = AddFromClipboardAsync(window);
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        var hotkeys = _services.GetRequiredService<HotkeyStatus>();
        if (!hotkeys.IsRegistered)
        {
            viewModel.ShowNotice(L.T("DesktopShell.HotkeyUnavailable"), hotkeys.Message, FAInfoBarSeverity.Warning);
        }
        else if (hotkeys.Message.Length > 0)
        {
            viewModel.ShowNotice(L.T("DesktopShell.FallbackShortcut"), hotkeys.Message, FAInfoBarSeverity.Informational);
        }

        return window;
    }

    /// <summary>
    /// Применяет настройки без перезапуска (PLAN.md, этап 9.1): «уменьшить движение» (настройка или выключенные эффекты
    /// анимации Windows), тема, акцент, фон, масштаб и компактность окон, поведение окна поиска, уровень журнала.
    /// </summary>
    // В высокой контрастности фон сплошной: полупрозрачный Mica и Acrylic с оттенком обоев мешают читать (этап 9.4).
    private static BackdropMode EffectiveBackdrop(AppearanceSettings appearance)
        => AppThemeService.IsHighContrast(Application.Current?.PlatformSettings) ? BackdropMode.Solid : appearance.Backdrop;

    private void ApplySettings(AppSettings settings)
    {
        var reduce = settings.Appearance.ReduceMotion || (OperatingSystem.IsWindows() && !NativeMethods.AnimationsEnabled());
        var appearance = settings.Appearance;
        var scale = Math.Clamp(appearance.FontScale, 0.9, 1.5);
        if (_quickSearch is not null)
        {
            _quickSearch.ReduceMotion = reduce;
            _quickSearch.HideOnDeactivate = settings.Popup.HideOnDeactivate;
            _quickSearch.ApplyAppearance(EffectiveBackdrop(appearance), scale, appearance.Compact);
        }

        _mainWindow?.ApplyAppearance(EffectiveBackdrop(appearance), scale, appearance.Compact);
        _services.GetRequiredService<MainViewModel>().ReduceMotion = reduce;
        _services.GetRequiredService<QuickSearchViewModel>().Display = new SearchDisplay(
            settings.Popup.ShowPath, settings.Popup.ShowSize, settings.Popup.ShowDate, settings.Search.ShowSuggestionsOnEmpty);

        var theme = appearance.Theme switch
        {
            Core.Settings.ThemeMode.Light => AppTheme.Light,
            Core.Settings.ThemeMode.Dark => AppTheme.Dark,
            _ => AppTheme.System,
        };
        // Применяется всегда, в том числе «как в системе» при старте: так включается высокая контрастность Windows (этап 9.4).
        _services.GetRequiredService<IThemeService>().Apply(theme);

        ApplyAccent(appearance);
        DiskApp.UI.Localization.Localizer.Instance.SetLanguage(appearance.Language);
        Program.LogLevel.MinimumLevel = settings.Diagnostics.LogLevel switch
        {
            "Debug" => Serilog.Events.LogEventLevel.Debug,
            "Warning" => Serilog.Events.LogEventLevel.Warning,
            "Error" => Serilog.Events.LogEventLevel.Error,
            _ => Serilog.Events.LogEventLevel.Information,
        };
    }

    /// <summary>Акцент: цвет Windows или свой из настроек (по умолчанию синий Windows).</summary>
    private static void ApplyAccent(AppearanceSettings appearance)
    {
        if (Application.Current?.Styles.OfType<FluentAvalonia.Styling.FluentAvaloniaTheme>().FirstOrDefault() is not { } theme)
        {
            return;
        }

        var custom = !appearance.UseSystemAccent && Avalonia.Media.Color.TryParse(appearance.AccentColor ?? "#0078D4", out var color)
            ? color
            : (Avalonia.Media.Color?)null;
        if (theme.PreferUserAccentColor == appearance.UseSystemAccent && theme.CustomAccentColor == custom)
        {
            return;
        }

        theme.PreferUserAccentColor = appearance.UseSystemAccent;
        theme.CustomAccentColor = custom;
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

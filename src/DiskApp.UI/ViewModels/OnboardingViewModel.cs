using System.Collections.ObjectModel;
using System.Text.Json.Nodes;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DiskApp.UI.Localization;
using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.Text;

using Symbol = FluentIcons.Common.Symbol;

namespace DiskApp.UI.ViewModels;

/// <summary>Шаг мастера первого запуска.</summary>
public enum OnboardingStep
{
    /// <summary>Папки для поиска.</summary>
    Folders,

    /// <summary>Горячая клавиша.</summary>
    Hotkey,

    /// <summary>Запуск вместе с Windows.</summary>
    Startup,

    /// <summary>Тема и язык.</summary>
    Appearance,

    /// <summary>Готово: идёт индексация.</summary>
    Done,
}

/// <summary>Папка, которую можно отметить для индекса.</summary>
public sealed partial class FolderChoice : ObservableObject
{
    /// <summary>Создаёт вариант.</summary>
    public FolderChoice(string path, string title, Symbol symbol, bool isChecked, bool isRoot)
    {
        Path = path;
        Title = title;
        Symbol = symbol;
        IsChecked = isChecked;
        IsRoot = isRoot;
    }

    /// <summary>Полный путь.</summary>
    public string Path { get; }

    /// <summary>Подпись: «Документы», «Загрузки» или имя папки.</summary>
    public string Title { get; }

    /// <summary>Значок.</summary>
    public Symbol Symbol { get; }

    /// <summary>Уже в индексе: снять отметку нельзя.</summary>
    public bool IsRoot { get; }

    /// <summary>Можно менять отметку.</summary>
    public bool CanToggle => !IsRoot;

    /// <summary>Отмечена для индекса.</summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}

/// <summary>
/// Мастер первого запуска (PLAN.md, этап 9.3): папки для поиска → горячая клавиша → автозапуск → тема и язык → готово
/// с ходом индексации. Отмеченные папки добавляются в индекс при переходе со первого шага, поэтому к последнему шагу
/// индексация уже идёт. «Пропустить» и «Готово» запоминают, что мастер пройден.
/// </summary>
public sealed partial class OnboardingViewModel : ViewModelBase, IDisposable
{
    private readonly IRootsService _roots;
    private readonly ISettingsService _settings;
    private readonly ISearchService _search;
    private readonly IThemeService _themes;
    private readonly IAutoStartService _autoStart;
    private readonly IDialogService? _dialogs;
    private readonly IAppCommands? _commands;
    private readonly IReadOnlyList<(string Path, string TitleKey, Symbol Symbol)> _suggestions;
    private CancellationTokenSource? _progress;
    private readonly bool _ready;

    /// <summary>
    /// Создаёт мастер; <c>suggestions</c> — предлагаемые папки, без них — стандартные папки пользователя Windows.
    /// </summary>
    public OnboardingViewModel(
        IRootsService roots,
        ISettingsService settings,
        ISearchService search,
        IThemeService themes,
        HotkeyStatus hotkeys,
        IAutoStartService? autoStart = null,
        IDialogService? dialogs = null,
        IAppCommands? commands = null,
        IReadOnlyList<(string Path, string TitleKey, Symbol Symbol)>? suggestions = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(themes);
        ArgumentNullException.ThrowIfNull(hotkeys);

        _roots = roots;
        _settings = settings;
        _search = search;
        _themes = themes;
        _autoStart = autoStart ?? new NullAutoStartService();
        _dialogs = dialogs;
        _commands = commands;
        _suggestions = suggestions ?? StandardFolders();
        Hotkeys = hotkeys;
        IsAutoStartSupported = _autoStart.IsSupported;
        StartWithWindows = _autoStart.IsSupported;
        SelectedTheme = Themes.FirstOrDefault(option => option.Theme == themes.Current) ?? Themes[0];
        SelectedLanguage = Languages[0];
        _ready = true;
        Localizer.Instance.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Мастер закрыт: пройден или пропущен.</summary>
    public event EventHandler? Completed;

    /// <summary>Действующая горячая клавиша.</summary>
    public HotkeyStatus Hotkeys { get; }

    /// <summary>Папки на выбор.</summary>
    public ObservableCollection<FolderChoice> Folders { get; } = [];

    /// <summary>Темы.</summary>
    public IReadOnlyList<ThemeOption> Themes { get; } =
    [
        new(AppTheme.System, L.T("Settings.AsInWindows")),
        new(AppTheme.Light, L.T("Settings.Light")),
        new(AppTheme.Dark, L.T("Settings.Dark")),
    ];

    /// <summary>Языки.</summary>
    public IReadOnlyList<SettingOption<string>> Languages { get; } =
    [
        new(Localizer.SystemLanguage, "Settings.LanguageSystem"),
        new("ru", "Русский"),
        new("en", "English"),
    ];

    /// <summary>Текущий шаг.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StepNumber), nameof(StepText), nameof(IsFolders), nameof(IsHotkey), nameof(IsStartup), nameof(IsAppearance), nameof(IsDone), nameof(CanGoBack), nameof(NextText), nameof(StepTitle), nameof(StepSubtitle))]
    public partial OnboardingStep Step { get; set; }

    /// <summary>«Шаг 2 из 5».</summary>
    public string StepText => L.F("Onboarding.StepOf", StepNumber, StepCount);

    /// <summary>Номер шага с единицы.</summary>
    public int StepNumber => (int)Step + 1;

    /// <summary>Всего шагов.</summary>
    public int StepCount { get; } = Enum.GetValues<OnboardingStep>().Length;

    /// <summary>Шаг «Папки».</summary>
    public bool IsFolders => Step == OnboardingStep.Folders;

    /// <summary>Шаг «Горячая клавиша».</summary>
    public bool IsHotkey => Step == OnboardingStep.Hotkey;

    /// <summary>Шаг «Автозапуск».</summary>
    public bool IsStartup => Step == OnboardingStep.Startup;

    /// <summary>Шаг «Тема и язык».</summary>
    public bool IsAppearance => Step == OnboardingStep.Appearance;

    /// <summary>Шаг «Готово».</summary>
    public bool IsDone => Step == OnboardingStep.Done;

    /// <summary>Можно вернуться назад.</summary>
    public bool CanGoBack => Step is not OnboardingStep.Folders and not OnboardingStep.Done;

    /// <summary>Подпись главной кнопки.</summary>
    public string NextText => Step == OnboardingStep.Done ? L.T("Onboarding.Finish") : L.T("Onboarding.Next");

    /// <summary>Заголовок шага.</summary>
    public string StepTitle => L.T("Onboarding.Title." + Step);

    /// <summary>Пояснение шага.</summary>
    public string StepSubtitle => Step == OnboardingStep.Hotkey
        ? L.F("Onboarding.Subtitle.Hotkey", Hotkeys.QuickSearchText)
        : L.T("Onboarding.Subtitle." + Step);

    /// <summary>Автозапуск поддерживается.</summary>
    public bool IsAutoStartSupported { get; }

    /// <summary>Запускать вместе с Windows.</summary>
    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    /// <summary>Тема.</summary>
    [ObservableProperty]
    public partial ThemeOption SelectedTheme { get; set; }

    /// <summary>Язык.</summary>
    [ObservableProperty]
    public partial SettingOption<string> SelectedLanguage { get; set; }

    /// <summary>Ход индексации на последнем шаге.</summary>
    [ObservableProperty]
    public partial string ProgressText { get; set; } = string.Empty;

    /// <summary>Индексация идёт.</summary>
    [ObservableProperty]
    public partial bool IsIndexing { get; set; }

    /// <summary>Сообщение о проблеме (папку не удалось добавить); пусто — всё хорошо.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    public partial string Note { get; set; } = string.Empty;

    /// <summary>Есть сообщение.</summary>
    public bool HasNote => Note.Length > 0;

    /// <summary>Завершается, когда загружены папки на выбор.</summary>
    public Task Initialization { get; private set; } = Task.CompletedTask;

    /// <inheritdoc />
    public void Dispose()
    {
        Localizer.Instance.LanguageChanged -= OnLanguageChanged;
        _progress?.Cancel();
        _progress?.Dispose();
        _progress = null;
    }

    /// <summary>Загружает папки: уже добавленные корни и стандартные папки пользователя.</summary>
    public Task LoadAsync()
    {
        Initialization = LoadFoldersAsync();
        return Initialization;
    }

    /// <summary>Стандартные папки пользователя Windows, которые есть на диске.</summary>
    public static IReadOnlyList<(string Path, string TitleKey, Symbol Symbol)> StandardFolders()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return
        [
            (Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Onboarding.Folder.Documents", Symbol.Document),
            (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Onboarding.Folder.Desktop", Symbol.Desktop),
            (Path.Combine(profile, "Downloads"), "Onboarding.Folder.Downloads", Symbol.ArrowDownload),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Onboarding.Folder.Pictures", Symbol.Image),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Onboarding.Folder.Videos", Symbol.Video),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Onboarding.Folder.Music", Symbol.MusicNote1),
        ];
    }

    private async Task LoadFoldersAsync()
    {
        IReadOnlyList<RootInfo> roots;
        try
        {
            roots = await _roots.GetRootsAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            roots = [];
            Note = L.T("Onboarding.CouldNotGetRoots") + exception.Message;
        }

        Folders.Clear();
        foreach (var root in roots)
        {
            Folders.Add(new FolderChoice(root.Path, root.Label, Symbol.Folder, isChecked: true, isRoot: true));
        }

        foreach (var (path, titleKey, symbol) in _suggestions)
        {
            if (path.Length > 0 && Directory.Exists(path) && !Folders.Any(folder => string.Equals(folder.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                // Документы и рабочий стол отмечены сразу: в них чаще всего ищут.
                var common = titleKey is "Onboarding.Folder.Documents" or "Onboarding.Folder.Desktop" or "Onboarding.Folder.Downloads";
                Folders.Add(new FolderChoice(path, L.T(titleKey), symbol, isChecked: common, isRoot: false));
            }
        }
    }

    /// <summary>Добавить свою папку в список.</summary>
    [RelayCommand]
    private async Task AddFolderAsync()
    {
        if (_dialogs is null || await _dialogs.PickFolderAsync(L.T("Onboarding.ChooseFolder")) is not { } path)
        {
            return;
        }

        if (Folders.FirstOrDefault(folder => string.Equals(folder.Path, path, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            existing.IsChecked = true;
            return;
        }

        Folders.Add(new FolderChoice(path, PathText.FileName(path), Symbol.Folder, isChecked: true, isRoot: false));
    }

    /// <summary>Следующий шаг; на последнем — закрыть мастер.</summary>
    [RelayCommand]
    private async Task NextAsync()
    {
        switch (Step)
        {
            case OnboardingStep.Folders:
                await AddCheckedFoldersAsync();
                break;
            case OnboardingStep.Startup:
                ApplyAutoStart();
                break;
            case OnboardingStep.Done:
                await FinishAsync();
                return;
        }

        Step++;
        if (Step == OnboardingStep.Done)
        {
            StartProgress();
        }
    }

    /// <summary>Предыдущий шаг.</summary>
    [RelayCommand]
    private void Back()
    {
        if (CanGoBack)
        {
            Step--;
        }
    }

    /// <summary>Пропустить мастер: настройки можно поменять позже.</summary>
    [RelayCommand]
    private Task SkipAsync() => FinishAsync();

    /// <summary>Открыть быстрый поиск, чтобы попробовать.</summary>
    [RelayCommand]
    private void TryQuickSearch() => _commands?.ShowQuickSearch();

    private async Task AddCheckedFoldersAsync()
    {
        var failed = new List<string>();
        foreach (var folder in Folders.Where(static folder => folder.IsChecked && !folder.IsRoot).ToList())
        {
            try
            {
                await _roots.AddAsync(folder.Path);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failed.Add($"{folder.Title}: {exception.Message}");
            }
        }

        Note = failed.Count > 0 ? L.T("Onboarding.NotAllFoldersAdded") + string.Join("; ", failed) : string.Empty;
    }

    private void ApplyAutoStart()
    {
        if (!_autoStart.IsSupported || _autoStart.IsEnabled == StartWithWindows)
        {
            return;
        }

        try
        {
            _autoStart.SetEnabled(StartWithWindows);
        }
        catch (InvalidOperationException exception)
        {
            Note = exception.Message;
        }
    }

    partial void OnSelectedThemeChanged(ThemeOption value)
    {
        if (value.Theme != _themes.Current)
        {
            _themes.Apply(value.Theme);
        }
    }

    /// <summary>Язык применяется сразу, чтобы мастер продолжился уже на нём.</summary>
    partial void OnSelectedLanguageChanged(SettingOption<string> value)
    {
        if (_ready && value is not null)
        {
            _ = SaveLanguageAsync(value.Value);
        }
    }

    private async Task SaveLanguageAsync(string language)
    {
        try
        {
            await _settings.PatchAsync(new JsonObject { ["appearance"] = new JsonObject { ["language"] = language } });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Note = exception.Message;
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(StepText));
        OnPropertyChanged(nameof(NextText));
        OnPropertyChanged(nameof(StepTitle));
        OnPropertyChanged(nameof(StepSubtitle));
    }

    private void StartProgress()
    {
        _progress?.Cancel();
        _progress?.Dispose();
        _progress = new CancellationTokenSource();
        _ = TrackProgressAsync(_progress.Token);
    }

    private async Task TrackProgressAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var status = await _search.GetIndexStatusAsync(cancellationToken);
                IsIndexing = status.IsIndexing;
                ProgressText = status.IsIndexing
                    ? L.F("Onboarding.Indexing", Formatting.Count(status.TotalEntries, L.T("Common.File.One"), L.T("Common.File.Few"), L.T("Common.File.Many")))
                    : L.F("Onboarding.Indexed", Formatting.Count(status.TotalEntries, L.T("Common.File.One"), L.T("Common.File.Few"), L.T("Common.File.Many")));
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Мастер закрыт.
        }
        catch (Exception exception)
        {
            ProgressText = exception.Message;
        }
    }

    private async Task FinishAsync()
    {
        _progress?.Cancel();
        try
        {
            await _settings.PatchAsync(new JsonObject
            {
                ["appearance"] = new JsonObject { ["theme"] = SelectedTheme.Theme.ToString(), ["language"] = SelectedLanguage.Value },
                ["onboarding"] = new JsonObject { ["completed"] = true, ["lastSeenVersion"] = AppVersion.Current },
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Мастер всё равно закрывается: в худшем случае он покажется при следующем запуске.
            Note = exception.Message;
        }

        Completed?.Invoke(this, EventArgs.Empty);
    }
}

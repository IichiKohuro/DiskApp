using DiskApp.Contracts.Search;

namespace DiskApp.Contracts.App;

/// <summary>
/// Сведения о приложении: <c>GET /api/v1/app/info</c>.
/// </summary>
/// <param name="Name">Название продукта.</param>
/// <param name="Version">Версия приложения.</param>
/// <param name="Runtime">Версия .NET.</param>
/// <param name="OperatingSystem">Операционная система.</param>
/// <param name="DataDirectory">Папка данных.</param>
/// <param name="IsPortable">Портативный режим.</param>
/// <param name="StartedUtc">Когда запущен сервер.</param>
/// <param name="CorruptDatabasePath">База была повреждена при старте и отложена сюда, индекс строится заново; иначе <c>null</c>.</param>
/// <param name="CorruptSettingsPath">Файл настроек был повреждён при старте и отложен сюда; иначе <c>null</c>.</param>
/// <param name="SettingsRestoredFromBackup">Повреждённые настройки восстановлены из резервной копии, а не сброшены.</param>
public sealed record AppInfoDto(
    string Name,
    string Version,
    string Runtime,
    string OperatingSystem,
    string DataDirectory,
    bool IsPortable,
    DateTimeOffset StartedUtc,
    string? CorruptDatabasePath = null,
    string? CorruptSettingsPath = null,
    bool SettingsRestoredFromBackup = false);

/// <summary>
/// Настройки приложения: <c>GET /api/v1/settings</c> и событие <c>OnSettingsChanged</c>.
/// <c>PATCH</c> принимает частичный JSON той же формы (JSON Merge Patch, RFC 7396): переданные поля заменяются,
/// <c>null</c> возвращает значение по умолчанию.
/// </summary>
public sealed record SettingsDto
{
    /// <summary>Горячие клавиши.</summary>
    public HotkeySettingsDto Hotkeys { get; set; } = new();

    /// <summary>Оформление.</summary>
    public AppearanceSettingsDto Appearance { get; set; } = new();

    /// <summary>Индексация.</summary>
    public IndexingSettingsDto Indexing { get; set; } = new();

    /// <summary>Поиск.</summary>
    public SearchSettingsDto Search { get; set; } = new();

    /// <summary>Трей и запуск.</summary>
    public TraySettingsDto Tray { get; set; } = new();

    /// <summary>Добавление файлов: «Входящие», недавние места, имя файла из буфера обмена.</summary>
    public AddSettingsDto Add { get; set; } = new();

    /// <summary>Экспериментальные функции.</summary>
    public ExperimentalSettingsDto Experimental { get; set; } = new();

    /// <summary>Окно быстрого поиска.</summary>
    public PopupSettingsDto Popup { get; set; } = new();

    /// <summary>Диагностика.</summary>
    public DiagnosticsSettingsDto Diagnostics { get; set; } = new();

    /// <summary>Знакомство с приложением.</summary>
    public OnboardingSettingsDto Onboarding { get; set; } = new();

    /// <summary>Разбор: накопители и рабочие папки.</summary>
    public SortingSettingsDto Sorting { get; set; } = new();
}

/// <summary>Разбор входящих: накопители и рабочие папки в порядке показа (не больше 50 в каждом списке).</summary>
public sealed record SortingSettingsDto
{
    /// <summary>Накопители — папки, куда сыплются файлы.</summary>
    public IReadOnlyList<SortingFolderDto> Inboxes { get; set; } = [];

    /// <summary>Рабочие папки; первые девять — на клавишах 1–9.</summary>
    public IReadOnlyList<SortingFolderDto> WorkFolders { get; set; } = [];

    /// <summary>Шаблон имени при переносе (<c>{имя}</c>, <c>{дата}</c>, <c>{время}</c>, <c>{теги}</c>, <c>{папка}</c>, <c>{№}</c>); пусто — без изменений.</summary>
    public string? NameTemplate { get; set; }

    /// <summary>Основная папка-накопитель — путь из <see cref="Inboxes"/>; пусто или папка не из списка — первая в списке.</summary>
    public string? MainInbox { get; set; }

    /// <summary>«Время успокоения»: файл накопителя считается пришедшим, когда не меняется столько секунд (0–600).</summary>
    public int SettleSeconds { get; set; } = 5;

    /// <summary>Glob-шаблоны имён временных и служебных файлов, которых нет в очереди накопителей (не больше 50).</summary>
    public IReadOnlyList<string> IgnorePatterns { get; set; } = [];
}

/// <summary>Папка разбора.</summary>
public sealed record SortingFolderDto
{
    /// <summary>Полный путь.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Подпись; пусто — имя папки.</summary>
    public string? Label { get; set; }

    /// <summary>У накопителя: в очередь попадают и файлы из подпапок.</summary>
    public bool IncludeSubfolders { get; set; }
}

/// <summary>Знакомство с приложением.</summary>
public sealed record OnboardingSettingsDto
{
    /// <summary>Мастер первого запуска пройден или пропущен.</summary>
    public bool Completed { get; set; }

    /// <summary>Версия, «Что нового» которой уже показано.</summary>
    public string? LastSeenVersion { get; set; }

    /// <summary>Показанные обучающие подсказки.</summary>
    public IReadOnlyList<string> TipsShown { get; set; } = [];
}

/// <summary>Горячие клавиши.</summary>
public sealed record HotkeySettingsDto
{
    /// <summary>Быстрый поиск, например <c>Ctrl+Alt+D</c>.</summary>
    public string QuickSearch { get; set; } = string.Empty;

    /// <summary>Разбор входящих.</summary>
    public string Triage { get; set; } = string.Empty;

    /// <summary>Quick Switch в диалогах «Сохранить» и «Открыть».</summary>
    public string QuickSwitch { get; set; } = string.Empty;

    /// <summary>Главное окно, например <c>Ctrl+Alt+O</c>.</summary>
    public string MainWindow { get; set; } = string.Empty;

    /// <summary>Окно Drag&amp;Drop, например <c>Ctrl+Alt+B</c>.</summary>
    public string DropWindow { get; set; } = string.Empty;
}

/// <summary>Добавление файлов.</summary>
public sealed record AddSettingsDto
{
    /// <summary>Папка «Входящие»; <c>null</c> — первый корень.</summary>
    public string? InboxPath { get; set; }

    /// <summary>Недавние папки назначения, самые свежие первыми (не больше 8).</summary>
    public IReadOnlyList<string> RecentTargets { get; set; } = [];

    /// <summary>Шаблон имени файла из буфера обмена: <c>{date}</c>, <c>{time}</c>, <c>{title}</c>.</summary>
    public string ClipboardNameTemplate { get; set; } = string.Empty;
}

/// <summary>Экспериментальные функции.</summary>
public sealed record ExperimentalSettingsDto
{
    /// <summary>Quick Switch для стандартных диалогов файлов (ADR-0008).</summary>
    public bool QuickSwitch { get; set; }
}

/// <summary>Окно быстрого поиска.</summary>
public sealed record PopupSettingsDto
{
    /// <summary>Монитор для окна.</summary>
    public PopupMonitor Monitor { get; set; }

    /// <summary>Прятать при потере фокуса.</summary>
    public bool HideOnDeactivate { get; set; }

    /// <summary>Показывать папку файла.</summary>
    public bool ShowPath { get; set; }

    /// <summary>Показывать размер.</summary>
    public bool ShowSize { get; set; }

    /// <summary>Показывать дату изменения.</summary>
    public bool ShowDate { get; set; }
}

/// <summary>Монитор для окна быстрого поиска.</summary>
public enum PopupMonitor
{
    /// <summary>С курсором мыши.</summary>
    Cursor,

    /// <summary>С активным окном.</summary>
    ActiveWindow,

    /// <summary>Основной.</summary>
    Primary,
}

/// <summary>Диагностика.</summary>
public sealed record DiagnosticsSettingsDto
{
    /// <summary>Уровень журнала: <c>Debug</c>, <c>Information</c>, <c>Warning</c>, <c>Error</c>.</summary>
    public string LogLevel { get; set; } = string.Empty;
}

/// <summary>Оформление.</summary>
public sealed record AppearanceSettingsDto
{
    /// <summary>Тема.</summary>
    public ThemeMode Theme { get; set; }

    /// <summary>Фон окон.</summary>
    public BackdropMode Backdrop { get; set; }

    /// <summary>Акцентный цвет системы.</summary>
    public bool UseSystemAccent { get; set; }

    /// <summary>Меньше анимаций.</summary>
    public bool ReduceMotion { get; set; }

    /// <summary>Аппаратное ускорение рендеринга (GPU); применяется после перезапуска.</summary>
    public bool HardwareAcceleration { get; set; }

    /// <summary>Свой акцентный цвет <c>#RRGGBB</c> при выключенном системном; <c>null</c> — синий по умолчанию.</summary>
    public string? AccentColor { get; set; }

    /// <summary>Масштаб текста: от 0,9 до 1,5.</summary>
    public double FontScale { get; set; }

    /// <summary>Компактный режим списков.</summary>
    public bool Compact { get; set; }

    /// <summary>Язык интерфейса: <c>system</c>, <c>ru</c> или <c>en</c>.</summary>
    public string Language { get; set; } = string.Empty;
}

/// <summary>Тема интерфейса.</summary>
public enum ThemeMode
{
    /// <summary>Как в системе.</summary>
    System,

    /// <summary>Светлая.</summary>
    Light,

    /// <summary>Тёмная.</summary>
    Dark,
}

/// <summary>Фон окон.</summary>
public enum BackdropMode
{
    /// <summary>Mica.</summary>
    Mica,

    /// <summary>Acrylic.</summary>
    Acrylic,

    /// <summary>Сплошной цвет.</summary>
    Solid,
}

/// <summary>Индексация.</summary>
public sealed record IndexingSettingsDto
{
    /// <summary>Правила исключений.</summary>
    public IReadOnlyList<ExclusionRuleDto> Exclusions { get; set; } = [];

    /// <summary>Скрытые файлы в новых корнях.</summary>
    public bool IncludeHiddenByDefault { get; set; }

    /// <summary>Системные файлы в новых корнях.</summary>
    public bool IncludeSystemByDefault { get; set; }

    /// <summary>Записей в пакете записи.</summary>
    public int BatchSize { get; set; }

    /// <summary>Пауза склейки событий наблюдателя, мс.</summary>
    public int WatcherDebounceMilliseconds { get; set; }

    /// <summary>Период плановой сверки, минуты; 0 — без неё.</summary>
    public int ReconcileIntervalMinutes { get; set; }

    /// <summary>Заходить внутрь junction и символических ссылок.</summary>
    public bool FollowReparsePoints { get; set; }

    /// <summary>Потоков обхода на корень; 0 — по типу диска.</summary>
    public int MaxScanParallelism { get; set; }
}

/// <summary>
/// Правило исключения.
/// </summary>
/// <param name="Kind">Вид правила.</param>
/// <param name="Pattern">Шаблон glob или порог (байты, глубина); для скрытых и системных — пусто.</param>
public sealed record ExclusionRuleDto(ExclusionKind Kind, string Pattern);

/// <summary>Вид правила исключения.</summary>
public enum ExclusionKind
{
    /// <summary>Шаблон glob по относительному пути.</summary>
    Glob,

    /// <summary>Скрытые файлы и папки.</summary>
    Hidden,

    /// <summary>Системные файлы и папки.</summary>
    System,

    /// <summary>Файлы больше порога в байтах.</summary>
    Size,

    /// <summary>Глубже заданного уровня.</summary>
    Depth,
}

/// <summary>Поиск.</summary>
public sealed record SearchSettingsDto
{
    /// <summary>Результатов на странице.</summary>
    public int PageSize { get; set; }

    /// <summary>Кандидатов до ранжирования (100…5 000).</summary>
    public int CandidateLimit { get; set; }

    /// <summary>Запросов в истории.</summary>
    public int HistoryLimit { get; set; }

    /// <summary>Период полураспада frecency, дни.</summary>
    public int FrecencyHalfLifeDays { get; set; }

    /// <summary>Пресет ранжирования.</summary>
    public RankingPreset RankingPreset { get; set; }

    /// <summary>Нечёткий поиск при малом числе точных совпадений.</summary>
    public bool Fuzzy { get; set; }

    /// <summary>Подборка при пустом запросе.</summary>
    public bool ShowSuggestionsOnEmpty { get; set; }
}

/// <summary>Трей и запуск.</summary>
public sealed record TraySettingsDto
{
    /// <summary>Запускаться свёрнутым в трей.</summary>
    public bool StartMinimized { get; set; }

    /// <summary>Закрытие окна сворачивает в трей.</summary>
    public bool CloseToTray { get; set; }

    /// <summary>Спрашивать при выходе во время операций.</summary>
    public bool ConfirmExitDuringOperations { get; set; }

    /// <summary>Запуск вместе с Windows.</summary>
    public bool AutoStart { get; set; }
}

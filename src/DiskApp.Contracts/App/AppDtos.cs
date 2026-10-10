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
public sealed record AppInfoDto(
    string Name,
    string Version,
    string Runtime,
    string OperatingSystem,
    string DataDirectory,
    bool IsPortable,
    DateTimeOffset StartedUtc);

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
}

/// <summary>Горячие клавиши.</summary>
public sealed record HotkeySettingsDto
{
    /// <summary>Быстрый поиск, например <c>Ctrl+Alt+D</c>.</summary>
    public string QuickSearch { get; set; } = string.Empty;

    /// <summary>Разбор входящих.</summary>
    public string Triage { get; set; } = string.Empty;
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

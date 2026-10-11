using DiskApp.Core.Domain;

namespace DiskApp.Core.Settings;

/// <summary>
/// Настройки приложения (PLAN.md, этап 1.5). Свойства с <c>set</c>, а не <c>init</c>: генератор System.Text.Json строит объект
/// инициализатором и затирает значения по умолчанию у отсутствующих в файле <c>init</c>-полей. Договорённость: экземпляр из
/// хранилища не меняется на месте, изменение — копия через <c>with</c> и сохранение через <c>ISettingsStore</c>.
/// Версия схемы растёт при несовместимом изменении полей; миграции описывает хранилище.
/// </summary>
public sealed record AppSettings
{
    /// <summary>Версия схемы настроек текущей сборки.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Настройки по умолчанию.</summary>
    public static AppSettings Default { get; } = new();

    /// <summary>Версия схемы файла.</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Горячие клавиши.</summary>
    public HotkeySettings Hotkeys { get; set; } = new();

    /// <summary>Внешний вид.</summary>
    public AppearanceSettings Appearance { get; set; } = new();

    /// <summary>Индексация.</summary>
    public IndexingSettings Indexing { get; set; } = new();

    /// <summary>Поиск.</summary>
    public SearchSettings Search { get; set; } = new();

    /// <summary>Трей и запуск.</summary>
    public TraySettings Tray { get; set; } = new();

    /// <summary>Добавление файлов: «Входящие», недавние места, имя файла из буфера обмена.</summary>
    public AddSettings Add { get; set; } = new();

    /// <summary>Экспериментальные функции: выключены по умолчанию.</summary>
    public ExperimentalSettings Experimental { get; set; } = new();

    /// <summary>Окно быстрого поиска: где показывать, когда прятать, какие подробности в строках.</summary>
    public PopupSettings Popup { get; set; } = new();

    /// <summary>Диагностика: уровень журнала.</summary>
    public DiagnosticsSettings Diagnostics { get; set; } = new();
}

/// <summary>
/// Окно быстрого поиска (PLAN.md, этап 9.1, «Поведение»).
/// </summary>
public sealed record PopupSettings
{
    /// <summary>На каком мониторе показывать окно.</summary>
    public PopupMonitor Monitor { get; set; } = PopupMonitor.Cursor;

    /// <summary>Прятать окно, когда оно теряет фокус.</summary>
    public bool HideOnDeactivate { get; set; } = true;

    /// <summary>Показывать папку файла в строке.</summary>
    public bool ShowPath { get; set; } = true;

    /// <summary>Показывать размер в строке.</summary>
    public bool ShowSize { get; set; } = true;

    /// <summary>Показывать дату изменения в строке.</summary>
    public bool ShowDate { get; set; } = true;
}

/// <summary>
/// Монитор для окна быстрого поиска.
/// </summary>
public enum PopupMonitor
{
    /// <summary>Монитор с курсором мыши.</summary>
    Cursor,

    /// <summary>Монитор с активным окном.</summary>
    ActiveWindow,

    /// <summary>Основной монитор.</summary>
    Primary,
}

/// <summary>
/// Диагностика (PLAN.md, этап 9.1, «Данные»).
/// </summary>
public sealed record DiagnosticsSettings
{
    /// <summary>Уровни журнала, которые можно выбрать.</summary>
    public static IReadOnlyList<string> LogLevels { get; } = ["Debug", "Information", "Warning", "Error"];

    /// <summary>Уровень журнала: <c>Debug</c>, <c>Information</c>, <c>Warning</c> или <c>Error</c>.</summary>
    public string LogLevel { get; set; } = "Information";
}

/// <summary>
/// Экспериментальные функции (PLAN.md, решение по вопросу 4): работают на внутренностях Windows, поэтому включаются
/// только явно.
/// </summary>
public sealed record ExperimentalSettings
{
    /// <summary>
    /// Quick Switch (этап 8.6, ADR-0008): сочетание <see cref="HotkeySettings.QuickSwitch"/> в стандартном диалоге
    /// «Сохранить» или «Открыть» переводит диалог в папку, выбранную в быстром поиске.
    /// </summary>
    public bool QuickSwitch { get; set; }
}

/// <summary>
/// Добавление файлов (PLAN.md, этап 8.5).
/// </summary>
public sealed record AddSettings
{
    /// <summary>Сколько недавних мест помнить.</summary>
    public const int RecentLimit = 8;

    /// <summary>Папка «Входящие» — место по умолчанию для добавленного; <c>null</c> — первый корень.</summary>
    public string? InboxPath { get; set; }

    /// <summary>Недавние папки назначения, самые свежие первыми.</summary>
    public IReadOnlyList<string> RecentTargets { get; set; } = [];

    /// <summary>Шаблон имени файла из буфера обмена: <c>{date}</c>, <c>{time}</c>, <c>{title}</c>.</summary>
    public string ClipboardNameTemplate { get; set; } = "{date}_{time}_{title}";

    /// <summary>Равенство по содержимому: недавние места сравниваются поэлементно, а не по ссылке.</summary>
    public bool Equals(AddSettings? other)
        => other is not null
            && InboxPath == other.InboxPath
            && ClipboardNameTemplate == other.ClipboardNameTemplate
            && RecentTargets.SequenceEqual(other.RecentTargets);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(InboxPath);
        hash.Add(ClipboardNameTemplate);
        foreach (var target in RecentTargets)
        {
            hash.Add(target);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// Горячие клавиши в виде текста <c>Ctrl+Alt+D</c>; разбор и регистрация — на стороне десктопа.
/// </summary>
public sealed record HotkeySettings
{
    /// <summary>Быстрый поиск (решение 15 плана, ADR-0006).</summary>
    public string QuickSearch { get; set; } = "Ctrl+Alt+D";

    /// <summary>Разбор входящих.</summary>
    public string Triage { get; set; } = "Ctrl+Alt+I";

    /// <summary>Quick Switch в диалогах «Сохранить» и «Открыть»; действует, только если функция включена.</summary>
    public string QuickSwitch { get; set; } = "Ctrl+Alt+G";
}

/// <summary>
/// Тема и фон окон.
/// </summary>
public sealed record AppearanceSettings
{
    /// <summary>Тема: вслед за системой, светлая или тёмная.</summary>
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>Фон главного окна.</summary>
    public BackdropMode Backdrop { get; set; } = BackdropMode.Mica;

    /// <summary>Акцентный цвет из системы.</summary>
    public bool UseSystemAccent { get; set; } = true;

    /// <summary>Уменьшить анимации независимо от системной настройки.</summary>
    public bool ReduceMotion { get; set; }

    /// <summary>Свой акцентный цвет <c>#RRGGBB</c>, когда системный выключен; <c>null</c> — синий Windows по умолчанию.</summary>
    public string? AccentColor { get; set; }

    /// <summary>Масштаб текста интерфейса: от 0,9 до 1,5.</summary>
    public double FontScale { get; set; } = 1.0;

    /// <summary>Компактный режим: строки списков ниже, отступы меньше.</summary>
    public bool Compact { get; set; }

    /// <summary>Язык интерфейса: <c>system</c> (по языку Windows), <c>ru</c> или <c>en</c> (этап 9.2).</summary>
    public string Language { get; set; } = "system";
}

/// <summary>
/// Тема интерфейса.
/// </summary>
public enum ThemeMode
{
    /// <summary>Как в системе.</summary>
    System,

    /// <summary>Светлая.</summary>
    Light,

    /// <summary>Тёмная.</summary>
    Dark,
}

/// <summary>
/// Фон окон.
/// </summary>
public enum BackdropMode
{
    /// <summary>Mica с fallback на сплошной цвет.</summary>
    Mica,

    /// <summary>Acrylic.</summary>
    Acrylic,

    /// <summary>Сплошной цвет темы.</summary>
    Solid,
}

/// <summary>
/// Параметры индексации.
/// </summary>
public sealed record IndexingSettings
{
    /// <summary>Правила исключения; по умолчанию <see cref="ExclusionRule.Defaults"/>.</summary>
    public IReadOnlyList<ExclusionRule> Exclusions { get; set; } = ExclusionRule.Defaults;

    /// <summary>Индексировать скрытые объекты у новых корней.</summary>
    public bool IncludeHiddenByDefault { get; set; }

    /// <summary>Индексировать системные объекты у новых корней.</summary>
    public bool IncludeSystemByDefault { get; set; }

    /// <summary>Размер пакета записи в базу.</summary>
    public int BatchSize { get; set; } = 5_000;

    /// <summary>Задержка склейки событий наблюдателя, мс.</summary>
    public int WatcherDebounceMilliseconds { get; set; } = 400;

    /// <summary>Период сверки индекса с диском, минуты; 0 — отключено.</summary>
    public int ReconcileIntervalMinutes { get; set; } = 360;

    /// <summary>Заходить ли внутрь junction и символических ссылок (с защитой от циклов).</summary>
    public bool FollowReparsePoints { get; set; }

    /// <summary>Потоков обхода на корень; 0 — по типу диска (SSD — 4, HDD — 1, сеть — 2).</summary>
    public int MaxScanParallelism { get; set; }

    /// <summary>Равенство по содержимому: список исключений сравнивается поэлементно, а не по ссылке.</summary>
    public bool Equals(IndexingSettings? other)
        => other is not null
            && IncludeHiddenByDefault == other.IncludeHiddenByDefault
            && IncludeSystemByDefault == other.IncludeSystemByDefault
            && BatchSize == other.BatchSize
            && WatcherDebounceMilliseconds == other.WatcherDebounceMilliseconds
            && ReconcileIntervalMinutes == other.ReconcileIntervalMinutes
            && FollowReparsePoints == other.FollowReparsePoints
            && MaxScanParallelism == other.MaxScanParallelism
            && Exclusions.SequenceEqual(other.Exclusions);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(IncludeHiddenByDefault);
        hash.Add(IncludeSystemByDefault);
        hash.Add(BatchSize);
        hash.Add(WatcherDebounceMilliseconds);
        hash.Add(ReconcileIntervalMinutes);
        hash.Add(FollowReparsePoints);
        hash.Add(MaxScanParallelism);
        foreach (var rule in Exclusions)
        {
            hash.Add(rule);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// Параметры поиска.
/// </summary>
public sealed record SearchSettings
{
    /// <summary>Размер страницы результатов.</summary>
    public int PageSize { get; set; } = 50;

    /// <summary>Максимум кандидатов из базы до ранжирования: 100…5 000 (ADR-0005); 2 000 держит p95 в бюджете (этап 3.6).</summary>
    public int CandidateLimit { get; set; } = 2_000;

    /// <summary>Сколько запросов хранить в истории.</summary>
    public int HistoryLimit { get; set; } = 100;

    /// <summary>Период полураспада frecency, дни.</summary>
    public int FrecencyHalfLifeDays { get; set; } = 30;

    /// <summary>Пресет весов ранжирования.</summary>
    public string RankingPreset { get; set; } = "balanced";

    /// <summary>Нечёткий поиск: при малом числе точных совпадений добавлять похожие имена с опечатками.</summary>
    public bool Fuzzy { get; set; } = true;

    /// <summary>Подборка при пустом запросе: недавние, закреплённые и изменённые файлы.</summary>
    public bool ShowSuggestionsOnEmpty { get; set; } = true;
}

/// <summary>
/// Поведение трея и запуска.
/// </summary>
public sealed record TraySettings
{
    /// <summary>Запускаться свёрнутым в трей.</summary>
    public bool StartMinimized { get; set; } = true;

    /// <summary>Закрытие главного окна прячет его в трей, а не завершает приложение.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>Спрашивать подтверждение выхода при активных операциях.</summary>
    public bool ConfirmExitDuringOperations { get; set; } = true;

    /// <summary>Автозапуск с Windows.</summary>
    public bool AutoStart { get; set; }
}

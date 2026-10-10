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

    /// <summary>Равенство по содержимому: список исключений сравнивается поэлементно, а не по ссылке.</summary>
    public bool Equals(IndexingSettings? other)
        => other is not null
            && IncludeHiddenByDefault == other.IncludeHiddenByDefault
            && IncludeSystemByDefault == other.IncludeSystemByDefault
            && BatchSize == other.BatchSize
            && WatcherDebounceMilliseconds == other.WatcherDebounceMilliseconds
            && ReconcileIntervalMinutes == other.ReconcileIntervalMinutes
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

    /// <summary>Максимум кандидатов из базы до ранжирования (ADR-0005).</summary>
    public int CandidateLimit { get; set; } = 5_000;

    /// <summary>Сколько запросов хранить в истории.</summary>
    public int HistoryLimit { get; set; } = 100;

    /// <summary>Период полураспада frecency, дни.</summary>
    public int FrecencyHalfLifeDays { get; set; } = 30;

    /// <summary>Пресет весов ранжирования.</summary>
    public string RankingPreset { get; set; } = "balanced";
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

using DiskApp.Core.Domain;
using DiskApp.Core.Settings;

namespace DiskApp.Core.Indexing;

/// <summary>
/// Параметры оркестратора индексации; собираются из настроек (<see cref="FromSettings"/>).
/// </summary>
public sealed record IndexerOptions
{
    /// <summary>Правила исключений.</summary>
    public IReadOnlyList<ExclusionRule> Exclusions { get; init; } = ExclusionRule.Defaults;

    /// <summary>Заходить ли внутрь junction и символических ссылок.</summary>
    public bool FollowReparsePoints { get; init; }

    /// <summary>Записей в пакете записи.</summary>
    public int BatchSize { get; init; } = ScanRequest.DefaultBatchSize;

    /// <summary>Потоков обхода на корень; 0 — по типу диска.</summary>
    public int MaxScanParallelism { get; init; }

    /// <summary>Пауза склейки событий наблюдателя.</summary>
    public TimeSpan WatcherQuiet { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Наибольшая задержка применения событий наблюдателя.</summary>
    public TimeSpan WatcherMaxDelay { get; init; } = TimeSpan.FromMilliseconds(900);

    /// <summary>Период плановой сверки корня; <see cref="TimeSpan.Zero"/> — без плановой сверки. Для сетевых корней вчетверо чаще.</summary>
    public TimeSpan ReconcileInterval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>Как часто проверять, не появился ли недоступный корень.</summary>
    public TimeSpan OfflinePollInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Сколько ждать ответа сетевого корня при проверке доступности: недоступная сетевая папка отвечает только
    /// по таймауту SMB, до минуты. Дольше — корень считается недоступным.
    /// </summary>
    public TimeSpan NetworkProbeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Шаг планировщика: проверки недоступных корней и плановых сверок.</summary>
    public TimeSpan SchedulerTick { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Сколько задач на разных дисках выполняется одновременно.</summary>
    public int MaxConcurrentTasks { get; init; } = 2;

    /// <summary>Сканировать все корни полностью при старте (индекс восстановлен после повреждения).</summary>
    public bool ForceFullScan { get; init; }

    /// <summary>
    /// Параметры из настроек индексации.
    /// </summary>
    /// <param name="settings">Настройки.</param>
    /// <param name="forceFullScan">Индекс создан заново: нужно полное сканирование всех корней.</param>
    public static IndexerOptions FromSettings(IndexingSettings settings, bool forceFullScan = false)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new IndexerOptions
        {
            Exclusions = settings.Exclusions,
            FollowReparsePoints = settings.FollowReparsePoints,
            BatchSize = settings.BatchSize > 0 ? settings.BatchSize : ScanRequest.DefaultBatchSize,
            MaxScanParallelism = Math.Max(0, settings.MaxScanParallelism),
            WatcherQuiet = TimeSpan.FromMilliseconds(Math.Clamp(settings.WatcherDebounceMilliseconds, 50, 2_000)),
            ReconcileInterval = settings.ReconcileIntervalMinutes > 0 ? TimeSpan.FromMinutes(settings.ReconcileIntervalMinutes) : TimeSpan.Zero,
            ForceFullScan = forceFullScan,
        };
    }
}

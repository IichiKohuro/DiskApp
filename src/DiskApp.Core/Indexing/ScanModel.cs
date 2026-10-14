using DiskApp.Core.Domain;

namespace DiskApp.Core.Indexing;

/// <summary>
/// Что сканировать: корень целиком или поддерево, с какими исключениями и сколькими потоками.
/// </summary>
public sealed record ScanRequest
{
    /// <summary>Размер пакета по умолчанию (PLAN.md, этап 2.2: 5–10 тыс.).</summary>
    public const int DefaultBatchSize = 5_000;

    /// <summary>Корень.</summary>
    public required Root Root { get; init; }

    /// <summary>Поддерево относительно корня; пустая строка — весь корень.</summary>
    public string RelPath { get; init; } = string.Empty;

    /// <summary>Сверка пишет найденные структурные изменения в историю файлов (этап 20).</summary>
    public bool Journal { get; init; } = true;

    /// <summary>Правила исключений.</summary>
    public required ExclusionMatcher Matcher { get; init; }

    /// <summary>Сколько потоков обходят папки верхнего уровня параллельно.</summary>
    public int DegreeOfParallelism { get; init; } = 1;

    /// <summary>Заходить ли внутрь junction и символических ссылок (с защитой от циклов).</summary>
    public bool FollowReparsePoints { get; init; }

    /// <summary>Записей в пакете.</summary>
    public int BatchSize { get; init; } = DefaultBatchSize;
}

/// <summary>
/// Пакет записей для конвейера записи. Папки идут раньше своих детей; пакеты одного потока обхода — в порядке обхода.
/// </summary>
/// <param name="Entries">Записи.</param>
public sealed record EntryBatch(IReadOnlyList<FileEntry> Entries);

/// <summary>
/// Ошибка обхода, не прервавшая сканирование.
/// </summary>
/// <param name="RelPath">Папка, которую не удалось прочитать.</param>
/// <param name="Message">Текст ошибки ОС.</param>
public sealed record ScanError(string RelPath, string Message);

/// <summary>
/// Итог обхода.
/// </summary>
public sealed record ScanSummary
{
    /// <summary>Сколько ошибок хранится подробно; остальные только считаются.</summary>
    public const int MaxStoredErrors = 1_000;

    /// <summary>Начальной папки нет: корень недоступен или поддерево удалено. Сверять и удалять записи нельзя.</summary>
    public bool StartMissing { get; init; }

    /// <summary>Начальная папка попадает под исключения.</summary>
    public bool StartExcluded { get; init; }

    /// <summary>Прочитано папок.</summary>
    public long Directories { get; init; }

    /// <summary>Найдено записей (файлов и папок), попавших в индекс.</summary>
    public long Entries { get; init; }

    /// <summary>Подробности первых <see cref="MaxStoredErrors"/> ошибок.</summary>
    public IReadOnlyList<ScanError> Errors { get; init; } = [];

    /// <summary>Всего ошибок.</summary>
    public long ErrorCount { get; init; }

    /// <summary>Папки, прочитанные не полностью: их записи в индексе нельзя удалять как исчезнувшие.</summary>
    public IReadOnlyList<string> FailedDirectories { get; init; } = [];

    /// <summary>Длительность обхода.</summary>
    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// Счётчики хода обхода, которые читает отчёт о прогрессе, пока потоки сканера их увеличивают.
/// </summary>
public sealed class ScanCounters
{
    private long _entries;
    private long _directories;
    private long _errors;
    private string? _currentPath;

    /// <summary>Найдено записей.</summary>
    public long Entries => Interlocked.Read(ref _entries);

    /// <summary>Прочитано папок.</summary>
    public long Directories => Interlocked.Read(ref _directories);

    /// <summary>Ошибок.</summary>
    public long Errors => Interlocked.Read(ref _errors);

    /// <summary>Папка, которую сейчас читает один из потоков.</summary>
    public string? CurrentPath => Volatile.Read(ref _currentPath);

    internal void AddEntries(long count) => Interlocked.Add(ref _entries, count);

    internal void AddDirectory() => Interlocked.Increment(ref _directories);

    internal void AddError() => Interlocked.Increment(ref _errors);

    internal void SetCurrentPath(string path) => Volatile.Write(ref _currentPath, path);
}

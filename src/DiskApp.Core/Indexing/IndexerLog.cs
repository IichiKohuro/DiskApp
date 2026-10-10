using Microsoft.Extensions.Logging;

namespace DiskApp.Core.Indexing;

/// <summary>
/// Сообщения журнала оркестратора индексации; генерируются заранее, без упаковки аргументов, когда уровень выключен.
/// </summary>
internal static partial class IndexerLog
{
    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "Индексатор запущен: {RootCount} корней")]
    public static partial void Started(ILogger logger, int rootCount);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Information, Message = "Индексация приостановлена")]
    public static partial void Paused(ILogger logger);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Information, Message = "Индексация возобновлена")]
    public static partial void Resumed(ILogger logger);

    [LoggerMessage(EventId = 2004, Level = LogLevel.Warning, Message = "Наблюдатель за {RootPath} не запущен")]
    public static partial void WatcherNotStarted(ILogger logger, Exception exception, string rootPath);

    [LoggerMessage(EventId = 2005, Level = LogLevel.Information, Message = "Корень {RootPath} недоступен: {Reason}")]
    public static partial void RootOffline(ILogger logger, string rootPath, string reason);

    [LoggerMessage(EventId = 2006, Level = LogLevel.Information, Message = "Корень {RootPath} снова доступен")]
    public static partial void RootBackOnline(ILogger logger, string rootPath);

    [LoggerMessage(EventId = 2007, Level = LogLevel.Information, Message = "Индексация {RootPath} [{RelPath}]: {Kind} ({Reason})")]
    public static partial void TaskStarted(ILogger logger, string rootPath, string relPath, IndexTaskKind kind, string reason);

    [LoggerMessage(
        EventId = 2008,
        Level = LogLevel.Information,
        Message = "Индексация {RootPath} [{RelPath}] завершена: {Status}, найдено {Found}, добавлено {Inserted}, обновлено {Updated}, удалено {Removed}, ошибок {Errors}, {ElapsedMs} мс")]
    public static partial void TaskCompleted(
        ILogger logger,
        string rootPath,
        string relPath,
        ScanOutcomeStatus status,
        long found,
        int inserted,
        int updated,
        int removed,
        long errors,
        long elapsedMs);

    [LoggerMessage(EventId = 2009, Level = LogLevel.Error, Message = "Индексация {RootPath} завершилась ошибкой")]
    public static partial void TaskFailed(ILogger logger, Exception exception, string rootPath);

    [LoggerMessage(EventId = 2010, Level = LogLevel.Warning, Message = "Состояние корня {RootPath} не сохранено")]
    public static partial void StatusNotSaved(ILogger logger, Exception exception, string rootPath);

    [LoggerMessage(EventId = 2011, Level = LogLevel.Warning, Message = "Запись в журнал сканирования не удалась")]
    public static partial void ScanLogFailed(ILogger logger, Exception exception);
}

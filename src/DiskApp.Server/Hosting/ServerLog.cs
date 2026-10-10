using Microsoft.Extensions.Logging;

namespace DiskApp.Server.Hosting;

/// <summary>
/// Сообщения журнала сервера (генератор LoggerMessage: без выделений, если уровень выключен).
/// </summary>
internal static partial class ServerLog
{
    [LoggerMessage(EventId = 4001, Level = LogLevel.Information, Message = "База {Path} открыта за {ElapsedMs} мс: схема v{SchemaVersion}, миграций применено {Applied}, создана заново: {Created}, восстановлена после повреждения: {Recovered}")]
    public static partial void DatabaseReady(ILogger logger, string path, long elapsedMs, int schemaVersion, int applied, bool created, bool recovered);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Information, Message = "Индексатор запущен за {ElapsedMs} мс")]
    public static partial void IndexerStarted(ILogger logger, long elapsedMs);

    [LoggerMessage(EventId = 4003, Level = LogLevel.Information, Message = "Индексатор остановлен")]
    public static partial void IndexerStopped(ILogger logger);

    [LoggerMessage(EventId = 4004, Level = LogLevel.Warning, Message = "Настройки {Path}: {Outcome}; повреждённый файл отложен в {CorruptPath}")]
    public static partial void SettingsRecovered(ILogger logger, string path, string outcome, string? corruptPath);

    [LoggerMessage(EventId = 4005, Level = LogLevel.Debug, Message = "HTTP {Method} {Path} → {StatusCode} за {ElapsedMs:F1} мс")]
    public static partial void Request(ILogger logger, string method, string path, int statusCode, double elapsedMs);

    [LoggerMessage(EventId = 4006, Level = LogLevel.Warning, Message = "HTTP {Method} {Path} → {StatusCode} за {ElapsedMs:F1} мс")]
    public static partial void RequestFailed(ILogger logger, string method, string path, int statusCode, double elapsedMs);
}

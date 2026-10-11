using System.Reflection;

using DiskApp.Infrastructure.Settings;
using DiskApp.Infrastructure.Sqlite;

namespace DiskApp.Server.Hosting;

/// <summary>
/// Состояние сервера, общее для endpoints и хоста: версия, время старта, итог открытия базы и чтения настроек.
/// </summary>
public sealed class ServerState
{
    private readonly TaskCompletionSource<DatabaseInitialization> _database = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Версия приложения из метаданных сборки.</summary>
    public string Version { get; } = typeof(ServerState).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(ServerState).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    /// <summary>Когда сервер начал запуск.</summary>
    public DateTimeOffset StartedUtc { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>Итог открытия базы; задача завершается, когда база готова.</summary>
    public Task<DatabaseInitialization> Database => _database.Task;

    /// <summary>База открыта.</summary>
    public bool IsDatabaseReady => _database.Task.IsCompletedSuccessfully;

    /// <summary>Итог чтения файла настроек; <c>null</c>, пока настройки не прочитаны.</summary>
    public SettingsLoadResult? Settings { get; private set; }

    internal void SetSettings(SettingsLoadResult result) => Settings = result;

    internal void MarkStarting(DateTimeOffset now) => StartedUtc = now;

    internal void SetDatabase(DatabaseInitialization initialization) => _database.TrySetResult(initialization);

    internal void FailDatabase(Exception exception) => _database.TrySetException(exception);
}

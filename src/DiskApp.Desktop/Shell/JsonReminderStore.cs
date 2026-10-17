using System.Text.Json;
using System.Text.Json.Serialization;

using DiskApp.UI.Services;

namespace DiskApp.Desktop.Shell;

/// <summary>
/// Очередь напоминаний между запусками (PLAN.md, этап 21.2): <c>reminders.json</c> рядом с настройками. Запись через
/// временный файл; испорченный файл не мешает запуску — очередь начнётся заново.
/// </summary>
internal sealed class JsonReminderStore(string path) : IReminderStore
{
    /// <inheritdoc />
    public IReadOnlyList<ReminderRequest> Load()
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, ReminderJsonContext.Default.ReminderRequestArray) ?? [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Serilog.Log.Warning(exception, "Очередь напоминаний не прочитана из {Path}", path);
            return [];
        }
    }

    /// <inheritdoc />
    public void Save(IReadOnlyList<ReminderRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        try
        {
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize([.. requests], ReminderJsonContext.Default.ReminderRequestArray));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(exception, "Очередь напоминаний не сохранена в {Path}", path);
        }
    }
}

/// <summary>Контекст сериализации очереди напоминаний.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ReminderRequest[]))]
internal sealed partial class ReminderJsonContext : JsonSerializerContext
{
}

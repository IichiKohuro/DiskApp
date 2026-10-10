using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using DiskApp.Core.Domain;
using DiskApp.Core.Settings;

namespace DiskApp.Infrastructure.Settings;

/// <summary>
/// Хранилище <see cref="AppSettings"/> в <c>settings.json</c>: сериализация через source generator (camelCase, перечисления строками,
/// отступы для правки руками), миграции по версиям схемы.
/// </summary>
public static class AppSettingsStore
{
    /// <summary>
    /// Миграции «с версии N»: ключ — версия файла, значение — правка JSON до чтения.
    /// Версия 0 — файлы без поля <c>schemaVersion</c>; полей для переноса в ней нет.
    /// </summary>
    public static IReadOnlyDictionary<int, Action<JsonObject>> Migrations { get; } = new Dictionary<int, Action<JsonObject>>
    {
        [0] = static _ => { },
    };

    /// <summary>
    /// Создаёт хранилище для файла; чтение — <see cref="JsonSettingsStore{T}.Load"/>.
    /// </summary>
    public static JsonSettingsStore<AppSettings> Create(string path)
        => new(path, AppSettingsJsonContext.Default.AppSettings, static () => AppSettings.Default, AppSettings.CurrentSchemaVersion, Migrations);
}

/// <summary>
/// Контекст сериализации настроек: без рефлексии, совместим с триммингом.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(ExclusionRule))]
public sealed partial class AppSettingsJsonContext : JsonSerializerContext
{
}

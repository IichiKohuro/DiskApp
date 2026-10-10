using System.Text.Json;
using System.Text.Json.Serialization;

using DiskApp.Contracts.App;

namespace DiskApp.Contracts;

/// <summary>
/// Контекст сериализации всех DTO API и хаба: без рефлексии, camelCase, перечисления строками (PLAN.md, этап 4.2).
/// Сервер и клиенты подключают его первым в цепочку резолверов.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HealthDto))]
public sealed partial class DiskAppJsonContext : JsonSerializerContext
{
}

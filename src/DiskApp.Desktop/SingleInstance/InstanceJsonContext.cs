using System.Text.Json.Serialization;

namespace DiskApp.Desktop.SingleInstance;

/// <summary>
/// Сгенерированный сериализатор для сообщений одиночного экземпляра: без рефлексии, дружит с обрезкой.
/// </summary>
[JsonSerializable(typeof(IReadOnlyList<string>))]
internal sealed partial class InstanceJsonContext : JsonSerializerContext
{
}

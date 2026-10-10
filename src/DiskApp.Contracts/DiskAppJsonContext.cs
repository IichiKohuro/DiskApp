using System.Text.Json;
using System.Text.Json.Serialization;

using DiskApp.Contracts.App;
using DiskApp.Contracts.Entries;
using DiskApp.Contracts.Hub;
using DiskApp.Contracts.Library;
using DiskApp.Contracts.Operations;
using DiskApp.Contracts.Roots;
using DiskApp.Contracts.Search;

namespace DiskApp.Contracts;

/// <summary>
/// Контекст сериализации всех DTO API и хаба: без рефлексии, camelCase, перечисления строками, <c>null</c> не пишется
/// (PLAN.md, этап 4.2). Сервер и клиенты подключают его первым в цепочку резолверов.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HealthDto))]
[JsonSerializable(typeof(AppInfoDto))]
[JsonSerializable(typeof(SettingsDto))]
[JsonSerializable(typeof(RootDto))]
[JsonSerializable(typeof(IReadOnlyList<RootDto>))]
[JsonSerializable(typeof(CreateRootRequest))]
[JsonSerializable(typeof(UpdateRootRequest))]
[JsonSerializable(typeof(IndexStatusDto))]
[JsonSerializable(typeof(IndexProgressDto))]
[JsonSerializable(typeof(RootStatusChangedDto))]
[JsonSerializable(typeof(EntryDto))]
[JsonSerializable(typeof(IReadOnlyList<EntryDto>))]
[JsonSerializable(typeof(BrowseResponse))]
[JsonSerializable(typeof(EntriesChangedDto))]
[JsonSerializable(typeof(SearchRequest))]
[JsonSerializable(typeof(SearchResponse))]
[JsonSerializable(typeof(SuggestResponse))]
[JsonSerializable(typeof(IReadOnlyList<HistoryEntryDto>))]
[JsonSerializable(typeof(OperationRequest))]
[JsonSerializable(typeof(OperationDto))]
[JsonSerializable(typeof(IReadOnlyList<OperationDto>))]
[JsonSerializable(typeof(OperationProgressDto))]
[JsonSerializable(typeof(OperationCompletedDto))]
[JsonSerializable(typeof(ConflictDto))]
[JsonSerializable(typeof(ConflictDecisionDto))]
[JsonSerializable(typeof(TagDto))]
[JsonSerializable(typeof(IReadOnlyList<TagDto>))]
[JsonSerializable(typeof(TagRequest))]
[JsonSerializable(typeof(EntryTagsRequest))]
[JsonSerializable(typeof(CollectionDto))]
[JsonSerializable(typeof(IReadOnlyList<CollectionDto>))]
[JsonSerializable(typeof(CollectionRequest))]
[JsonSerializable(typeof(CollectionItemsRequest))]
[JsonSerializable(typeof(FavoriteRequest))]
[JsonSerializable(typeof(FavoritesOrderRequest))]
[JsonSerializable(typeof(HubStatusDto))]
public sealed partial class DiskAppJsonContext : JsonSerializerContext
{
}

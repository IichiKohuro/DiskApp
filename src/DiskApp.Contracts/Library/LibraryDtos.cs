namespace DiskApp.Contracts.Library;

/// <summary>
/// Тег.
/// </summary>
/// <param name="Id">Идентификатор.</param>
/// <param name="Name">Имя.</param>
/// <param name="Color">Цвет <c>#RRGGBB</c>.</param>
public sealed record TagDto(long Id, string Name, string? Color);

/// <summary>
/// Создание и изменение тега: <c>POST /api/v1/tags</c>, <c>PUT /api/v1/tags/{id}</c>.
/// </summary>
public sealed record TagRequest
{
    /// <summary>Имя; уникально без учёта регистра.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Цвет <c>#RRGGBB</c>.</summary>
    public string? Color { get; set; }
}

/// <summary>
/// Теги записи: <c>POST /api/v1/entries/{id}/tags</c> заменяет набор тегов записи.
/// </summary>
public sealed record EntryTagsRequest
{
    /// <summary>Теги записи.</summary>
    public IReadOnlyList<long> TagIds { get; set; } = [];
}

/// <summary>
/// Коллекция: ручной набор записей или сохранённый запрос.
/// </summary>
/// <param name="Id">Идентификатор.</param>
/// <param name="Name">Имя.</param>
/// <param name="Kind">Вид.</param>
/// <param name="Query">Запрос сохранённого поиска.</param>
public sealed record CollectionDto(long Id, string Name, CollectionKind Kind, string? Query);

/// <summary>Вид коллекции.</summary>
public enum CollectionKind
{
    /// <summary>Записи добавлены вручную.</summary>
    Manual,

    /// <summary>Сохранённый запрос.</summary>
    SavedQuery,
}

/// <summary>
/// Создание и изменение коллекции: <c>POST /api/v1/collections</c>, <c>PUT /api/v1/collections/{id}</c>.
/// </summary>
public sealed record CollectionRequest
{
    /// <summary>Имя.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Вид.</summary>
    public CollectionKind Kind { get; set; }

    /// <summary>Запрос сохранённого поиска.</summary>
    public string? Query { get; set; }
}

/// <summary>
/// Элементы ручной коллекции: <c>PUT /api/v1/collections/{id}/items</c> заменяет состав и порядок.
/// </summary>
public sealed record CollectionItemsRequest
{
    /// <summary>Записи по порядку.</summary>
    public IReadOnlyList<long> EntryIds { get; set; } = [];
}

/// <summary>
/// Добавление в избранное: <c>POST /api/v1/favorites</c>.
/// </summary>
public sealed record FavoriteRequest
{
    /// <summary>Запись.</summary>
    public long EntryId { get; set; }
}

/// <summary>
/// Порядок избранного: <c>PUT /api/v1/favorites/order</c>.
/// </summary>
public sealed record FavoritesOrderRequest
{
    /// <summary>Записи избранного в новом порядке.</summary>
    public IReadOnlyList<long> EntryIds { get; set; } = [];
}

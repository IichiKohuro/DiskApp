using System.Text.RegularExpressions;

using DiskApp.Contracts;
using DiskApp.Contracts.Entries;
using DiskApp.Contracts.Library;
using DiskApp.Core.Abstractions;
using DiskApp.Core.Domain;
using DiskApp.Core.Repositories;
using DiskApp.Server.Localization;
using DiskApp.Server.Mapping;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace DiskApp.Server.Endpoints;

/// <summary>
/// Избранное, недавние, теги и коллекции (PLAN.md, этап 4.3).
/// </summary>
internal static partial class LibraryEndpoints
{
    public static void MapLibrary(this IEndpointRouteBuilder endpoints)
    {
        var favorites = endpoints.MapGroup(string.Empty).WithTags("Favorites");

        favorites.MapGet(ApiRoutes.V1.Favorites, GetFavoritesAsync)
            .WithName("GetFavorites")
            .WithSummary("Избранное")
            .WithDescription("Записи избранного в пользовательском порядке.");

        favorites.MapGet(ApiRoutes.V1.Pinned, GetFavoritesAsync)
            .WithName("GetPinned")
            .WithSummary("Закреплённое на главной")
            .WithDescription("Сейчас совпадает с избранным.");

        favorites.MapPost(ApiRoutes.V1.Favorites, AddFavoriteAsync)
            .WithName("AddFavorite")
            .WithSummary("Добавить в избранное")
            .WithDescription("Запись встаёт в конец списка; повторное добавление ничего не меняет.");

        favorites.MapDelete(ApiRoutes.V1.FavoriteByEntry, RemoveFavoriteAsync)
            .WithName("RemoveFavorite")
            .WithSummary("Убрать из избранного");

        favorites.MapPut(ApiRoutes.V1.FavoritesOrder, ReorderFavoritesAsync)
            .WithName("ReorderFavorites")
            .WithSummary("Порядок избранного")
            .WithDescription("Полный список записей избранного в новом порядке.");

        endpoints.MapGet(ApiRoutes.V1.Recent, GetRecentAsync)
            .WithTags("Favorites")
            .WithName("GetRecent")
            .WithSummary("Недавно открытые")
            .WithDescription("Записи по времени последнего открытия через DiskApp; limit — до 200.");

        var tags = endpoints.MapGroup(string.Empty).WithTags("Tags");

        tags.MapGet(ApiRoutes.V1.Tags, GetTagsAsync).WithName("GetTags").WithSummary("Теги");
        tags.MapPost(ApiRoutes.V1.Tags, CreateTagAsync)
            .WithName("CreateTag")
            .WithSummary("Создать тег")
            .WithDescription("Имя уникально без учёта регистра; цвет — #RRGGBB.");
        tags.MapPut(ApiRoutes.V1.TagById, UpdateTagAsync).WithName("UpdateTag").WithSummary("Изменить тег");
        tags.MapDelete(ApiRoutes.V1.TagById, DeleteTagAsync)
            .WithName("DeleteTag")
            .WithSummary("Удалить тег")
            .WithDescription("Тег снимается со всех записей.");
        tags.MapGet(ApiRoutes.V1.AutoTags, GetAutoTagsAsync).WithName("GetAutoTags").WithSummary("Авто-теги папок");
        tags.MapPut(ApiRoutes.V1.AutoTags, SetAutoTagsAsync)
            .WithName("SetAutoTags")
            .WithSummary("Заменить авто-теги папки")
            .WithDescription("Тег наследует всё внутри папки на любой глубине — и то, что появится позже; пустой список снимает все. Недостающие теги создаются.")
            .ProducesProblem(StatusCodes.Status403Forbidden);

        var collections = endpoints.MapGroup(string.Empty).WithTags("Collections");

        collections.MapGet(ApiRoutes.V1.Collections, GetCollectionsAsync).WithName("GetCollections").WithSummary("Коллекции");
        collections.MapPost(ApiRoutes.V1.Collections, CreateCollectionAsync)
            .WithName("CreateCollection")
            .WithSummary("Создать коллекцию")
            .WithDescription("Ручная коллекция — набор записей; сохранённый запрос — строка поиска (Query обязателен).");
        collections.MapPut(ApiRoutes.V1.CollectionById, UpdateCollectionAsync).WithName("UpdateCollection").WithSummary("Изменить коллекцию");
        collections.MapDelete(ApiRoutes.V1.CollectionById, DeleteCollectionAsync).WithName("DeleteCollection").WithSummary("Удалить коллекцию");
        collections.MapGet(ApiRoutes.V1.CollectionItems, GetCollectionItemsAsync)
            .WithName("GetCollectionItems")
            .WithSummary("Записи ручной коллекции")
            .WithDescription("Для сохранённого запроса — 400: его результаты выдаёт поиск.");
        collections.MapPut(ApiRoutes.V1.CollectionItems, SetCollectionItemsAsync)
            .WithName("SetCollectionItems")
            .WithSummary("Состав ручной коллекции")
            .WithDescription("Заменяет состав и порядок; неизвестная запись — 400.");
    }

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex ColorPattern();

    private static async Task<Ok<IReadOnlyList<EntryDto>>> GetFavoritesAsync(IFavoriteRepository favorites, IFileIndexRepository entries, EntryLookup lookup, CancellationToken cancellationToken)
    {
        var ordered = (await favorites.GetAllAsync(cancellationToken).ConfigureAwait(false)).OrderBy(static favorite => favorite.Order).Select(static favorite => favorite.EntryId).ToArray();
        return TypedResults.Ok(await EntriesInOrderAsync(ordered, entries, lookup, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<Results<Ok<EntryDto>, ProblemHttpResult>> AddFavoriteAsync(
        FavoriteRequest request,
        IFavoriteRepository favorites,
        IClock clock,
        EntryLookup lookup,
        CancellationToken cancellationToken)
    {
        if (await lookup.GetAsync(request.EntryId, cancellationToken).ConfigureAwait(false) is null)
        {
            return ApiProblems.NotFound(L.F("Library.EntryNotFound", request.EntryId));
        }

        await favorites.AddAsync(request.EntryId, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok((await lookup.GetAsync(request.EntryId, cancellationToken).ConfigureAwait(false))!);
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> RemoveFavoriteAsync(long entryId, IFavoriteRepository favorites, CancellationToken cancellationToken)
        => await favorites.RemoveAsync(entryId, cancellationToken).ConfigureAwait(false)
            ? TypedResults.NoContent()
            : ApiProblems.NotFound(L.F("LibraryEndpoints.EntryIsNotInFavorites", entryId));

    private static async Task<Results<NoContent, ValidationProblem>> ReorderFavoritesAsync(FavoritesOrderRequest request, IFavoriteRepository favorites, CancellationToken cancellationToken)
    {
        var current = (await favorites.GetAllAsync(cancellationToken).ConfigureAwait(false)).Select(static favorite => favorite.EntryId).ToHashSet();
        if (request.EntryIds.Count != current.Count || request.EntryIds.Distinct().Count() != current.Count || !request.EntryIds.All(current.Contains))
        {
            return ApiProblems.Invalid(nameof(request.EntryIds), L.T("LibraryEndpoints.PassEveryFavoriteExactlyOnce"));
        }

        await favorites.ReorderAsync(request.EntryIds, cancellationToken).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<IReadOnlyList<EntryDto>>, ValidationProblem>> GetRecentAsync(
        int? limit,
        IUsageRepository usage,
        IFileIndexRepository entries,
        EntryLookup lookup,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 200)
        {
            return ApiProblems.Invalid("limit", L.T("LibraryEndpoints.EntriesFrom1To200"));
        }

        var recent = await usage.GetRecentAsync(limit ?? 20, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(await EntriesInOrderAsync(recent.Select(static stat => stat.EntryId).ToArray(), entries, lookup, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IReadOnlyList<EntryDto>> EntriesInOrderAsync(long[] ids, IFileIndexRepository entries, EntryLookup lookup, CancellationToken cancellationToken)
    {
        var byId = (await entries.GetManyAsync(ids, cancellationToken).ConfigureAwait(false)).ToDictionary(static entry => entry.Id);
        var ordered = ids.Where(byId.ContainsKey).Select(id => byId[id]).ToArray();
        return await lookup.ToDtosAsync(ordered, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Ok<TagDto[]>> GetTagsAsync(ITagRepository tags, CancellationToken cancellationToken)
        => TypedResults.Ok((await tags.GetAllAsync(cancellationToken).ConfigureAwait(false)).Select(static tag => tag.ToDto()).ToArray());

    private static async Task<Results<Created<TagDto>, ValidationProblem, ProblemHttpResult>> CreateTagAsync(TagRequest request, ITagRepository tags, CancellationToken cancellationToken)
    {
        if (ValidateTag(request) is { } invalid)
        {
            return invalid;
        }

        if (await tags.GetByNameAsync(request.Name.Trim(), cancellationToken).ConfigureAwait(false) is not null)
        {
            return ApiProblems.Conflict("tag.duplicate", L.F("Library.TagExists", request.Name.Trim()));
        }

        var tag = await tags.AddAsync(request.Name.Trim(), request.Color, cancellationToken).ConfigureAwait(false);
        return TypedResults.Created(ApiRoutes.V1.Tag(tag.Id), tag.ToDto());
    }

    private static async Task<Results<Ok<TagDto>, ValidationProblem, ProblemHttpResult>> UpdateTagAsync(long id, TagRequest request, ITagRepository tags, CancellationToken cancellationToken)
    {
        if (ValidateTag(request) is { } invalid)
        {
            return invalid;
        }

        if (await tags.GetByIdAsync(id, cancellationToken).ConfigureAwait(false) is null)
        {
            return ApiProblems.NotFound(L.F("Library.TagNotFound", id));
        }

        if (await tags.GetByNameAsync(request.Name.Trim(), cancellationToken).ConfigureAwait(false) is { } other && other.Id != id)
        {
            return ApiProblems.Conflict("tag.duplicate", L.F("Library.TagExists", request.Name.Trim()));
        }

        var updated = new Tag(id, request.Name.Trim(), request.Color);
        await tags.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(updated.ToDto());
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteTagAsync(long id, ITagRepository tags, CancellationToken cancellationToken)
        => await tags.DeleteAsync(id, cancellationToken).ConfigureAwait(false)
            ? TypedResults.NoContent()
            : ApiProblems.NotFound(L.F("Library.TagNotFound", id));

    private static async Task<Ok<FolderTagsDto[]>> GetAutoTagsAsync(IFolderTagRepository folderTags, CancellationToken cancellationToken)
        => TypedResults.Ok((await folderTags.GetAllAsync(cancellationToken).ConfigureAwait(false)).Select(static folder => folder.ToDto()).ToArray());

    /// <summary>
    /// Авто-теги папки (этап 19): папка — внутри корней и существует, теги по имени (недостающие создаются). Кэш поиска
    /// сбрасывается: «tag:» должен сразу находить файлы папки.
    /// </summary>
    private static async Task<Results<Ok<FolderTagsDto>, ValidationProblem, ProblemHttpResult>> SetAutoTagsAsync(
        SetFolderTagsRequest request,
        IFolderTagRepository folderTags,
        ITagRepository tags,
        RootGuard guard,
        Core.Search.SearchService search,
        Core.Paths.PathNormalizer paths,
        CancellationToken cancellationToken)
    {
        var names = request.Tags.Select(static name => name?.Trim() ?? string.Empty).Where(static name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length > SetFolderTagsRequest.MaxTags)
        {
            return ApiProblems.Invalid(nameof(request.Tags), L.F("LibraryEndpoints.TooManyAutoTags", SetFolderTagsRequest.MaxTags));
        }

        if (names.Any(static name => name.Length > 64))
        {
            return ApiProblems.Invalid(nameof(request.Tags), L.T("LibraryEndpoints.ATagNameIs1To"));
        }

        var (allowed, path, _) = await guard.CheckAsync(request.Path, cancellationToken).ConfigureAwait(false);
        if (!allowed)
        {
            return path is null
                ? ApiProblems.Invalid(nameof(request.Path), L.F("OperationEndpoints.PathMustBeAbsolute", request.Path))
                : TypedResults.Problem(L.F("OperationEndpoints.PathIsOutsideTheAddedRoots", path), statusCode: StatusCodes.Status403Forbidden, title: L.T("Operations.AccessDenied"));
        }

        // Снять авто-теги можно и у пропавшей папки: её перенесли через Проводник, а строки остались.
        if (names.Length > 0 && !Directory.Exists(path))
        {
            return ApiProblems.NotFound(L.F("OperationEndpoints.FolderNotFound", path));
        }

        var ids = new List<long>(names.Length);
        foreach (var name in names)
        {
            ids.Add((await tags.AddAsync(name, cancellationToken: cancellationToken).ConfigureAwait(false)).Id);
        }

        await folderTags.SetAsync(path!, ids, cancellationToken).ConfigureAwait(false);
        search.InvalidateCaches();
        var saved = (await folderTags.GetAllAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(folder => paths.Comparer.Equals(folder.Path, path));
        return TypedResults.Ok(saved?.ToDto() ?? new FolderTagsDto(path!, []));
    }

    private static ValidationProblem? ValidateTag(TagRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 64)
        {
            errors[nameof(request.Name)] = [L.T("LibraryEndpoints.ATagNameIs1To")];
        }

        if (request.Color is not null && !ColorPattern().IsMatch(request.Color))
        {
            errors[nameof(request.Color)] = [L.T("LibraryEndpoints.AColorLooksLikeRRGGBB")];
        }

        return errors.Count > 0 ? ApiProblems.Invalid(errors) : null;
    }

    private static async Task<Ok<CollectionDto[]>> GetCollectionsAsync(ICollectionRepository collections, CancellationToken cancellationToken)
        => TypedResults.Ok((await collections.GetAllAsync(cancellationToken).ConfigureAwait(false)).Select(static collection => collection.ToDto()).ToArray());

    private static async Task<Results<Created<CollectionDto>, ValidationProblem>> CreateCollectionAsync(CollectionRequest request, ICollectionRepository collections, CancellationToken cancellationToken)
    {
        if (ValidateCollection(request) is { } invalid)
        {
            return invalid;
        }

        var collection = await collections.AddAsync(new Collection(0, request.Name.Trim(), request.Kind.ToDomain(), Query(request)), cancellationToken).ConfigureAwait(false);
        return TypedResults.Created(ApiRoutes.V1.Collection(collection.Id), collection.ToDto());
    }

    private static async Task<Results<Ok<CollectionDto>, ValidationProblem, ProblemHttpResult>> UpdateCollectionAsync(
        long id,
        CollectionRequest request,
        ICollectionRepository collections,
        CancellationToken cancellationToken)
    {
        if (ValidateCollection(request) is { } invalid)
        {
            return invalid;
        }

        var updated = new Collection(id, request.Name.Trim(), request.Kind.ToDomain(), Query(request));
        return await collections.UpdateAsync(updated, cancellationToken).ConfigureAwait(false)
            ? TypedResults.Ok(updated.ToDto())
            : ApiProblems.NotFound(L.F("Library.CollectionNotFound", id));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteCollectionAsync(long id, ICollectionRepository collections, CancellationToken cancellationToken)
        => await collections.DeleteAsync(id, cancellationToken).ConfigureAwait(false)
            ? TypedResults.NoContent()
            : ApiProblems.NotFound(L.F("Library.CollectionNotFound", id));

    private static async Task<Results<Ok<IReadOnlyList<EntryDto>>, ValidationProblem, ProblemHttpResult>> GetCollectionItemsAsync(
        long id,
        ICollectionRepository collections,
        IFileIndexRepository entries,
        EntryLookup lookup,
        CancellationToken cancellationToken)
    {
        var collection = await collections.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (collection is null)
        {
            return ApiProblems.NotFound(L.F("Library.CollectionNotFound", id));
        }

        if (collection.Kind == Core.Domain.CollectionKind.SavedQuery)
        {
            return ApiProblems.Invalid("id", L.T("LibraryEndpoints.ThisIsASavedQueryIts"));
        }

        var ids = await collections.GetItemsAsync(id, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(await EntriesInOrderAsync([.. ids], entries, lookup, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> SetCollectionItemsAsync(
        long id,
        CollectionItemsRequest request,
        ICollectionRepository collections,
        IFileIndexRepository entries,
        CancellationToken cancellationToken)
    {
        var collection = await collections.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (collection is null)
        {
            return ApiProblems.NotFound(L.F("Library.CollectionNotFound", id));
        }

        if (collection.Kind == Core.Domain.CollectionKind.SavedQuery)
        {
            return ApiProblems.Invalid("id", L.T("LibraryEndpoints.EntriesAreNotAddedToA"));
        }

        var desired = request.EntryIds.Distinct().ToArray();
        var existing = (await entries.GetManyAsync(desired, cancellationToken).ConfigureAwait(false)).Select(static entry => entry.Id).ToHashSet();
        var unknown = desired.Where(entryId => !existing.Contains(entryId)).ToArray();
        if (unknown.Length > 0)
        {
            return ApiProblems.Invalid(nameof(request.EntryIds), L.T("LibraryEndpoints.UnknownEntries") + string.Join(", ", unknown) + ".");
        }

        var current = await collections.GetItemsAsync(id, cancellationToken).ConfigureAwait(false);
        foreach (var entryId in current.Except(desired))
        {
            await collections.RemoveItemAsync(id, entryId, cancellationToken).ConfigureAwait(false);
        }

        foreach (var entryId in desired.Except(current))
        {
            await collections.AddItemAsync(id, entryId, cancellationToken).ConfigureAwait(false);
        }

        await collections.ReorderItemsAsync(id, desired, cancellationToken).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    private static ValidationProblem? ValidateCollection(CollectionRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 128)
        {
            errors[nameof(request.Name)] = [L.T("LibraryEndpoints.ACollectionNameIs1To")];
        }

        if (request.Kind == Contracts.Library.CollectionKind.SavedQuery && string.IsNullOrWhiteSpace(request.Query))
        {
            errors[nameof(request.Query)] = [L.T("LibraryEndpoints.ASavedQueryNeedsASearch")];
        }

        return errors.Count > 0 ? ApiProblems.Invalid(errors) : null;
    }

    private static string? Query(CollectionRequest request)
        => request.Kind == Contracts.Library.CollectionKind.SavedQuery ? request.Query?.Trim() : null;
}

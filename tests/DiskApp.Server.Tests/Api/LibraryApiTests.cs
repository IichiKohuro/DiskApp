using System.Net;
using System.Net.Http.Json;

using DiskApp.Contracts;
using DiskApp.Contracts.Library;

namespace DiskApp.Server.Tests.Api;

/// <summary>
/// Избранное, теги и коллекции через API.
/// </summary>
public sealed class LibraryApiTests
{
    [Fact]
    public async Task Favorites_AddListReorderRemove()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        context.Files.CreateFile("one.txt", "1");
        context.Files.CreateFile("two.txt", "2");
        await context.AddRootAsync();
        var items = (await context.GetAsync(ApiRoutes.V1.Search + "?q=ext:txt&sort=Name&desc=false", DiskAppJsonContext.Default.SearchResponse)).Items;
        var one = items[0].Id;
        var two = items[1].Id;

        // Act
        await PostFavoriteAsync(context, one);
        var added = await PostFavoriteAsync(context, two);
        var listed = await context.GetAsync(ApiRoutes.V1.Favorites, DiskAppJsonContext.Default.IReadOnlyListEntryDto);
        var reorder = await context.Client.PutAsJsonAsync(ApiRoutes.V1.FavoritesOrder, new FavoritesOrderRequest { EntryIds = [two, one] },
            DiskAppJsonContext.Default.FavoritesOrderRequest, TestContext.Current.CancellationToken);
        var badOrder = await context.Client.PutAsJsonAsync(ApiRoutes.V1.FavoritesOrder, new FavoritesOrderRequest { EntryIds = [two] },
            DiskAppJsonContext.Default.FavoritesOrderRequest, TestContext.Current.CancellationToken);
        var pinned = await context.GetAsync(ApiRoutes.V1.Pinned, DiskAppJsonContext.Default.IReadOnlyListEntryDto);
        var remove = await context.Client.DeleteAsync(new Uri(ApiRoutes.V1.Favorites + "/" + one, UriKind.Relative), TestContext.Current.CancellationToken);
        var removeAgain = await context.Client.DeleteAsync(new Uri(ApiRoutes.V1.Favorites + "/" + one, UriKind.Relative), TestContext.Current.CancellationToken);
        var unknown = await context.Client.PostAsJsonAsync(ApiRoutes.V1.Favorites, new FavoriteRequest { EntryId = 999 }, DiskAppJsonContext.Default.FavoriteRequest, TestContext.Current.CancellationToken);

        // Assert
        added.IsFavorite.ShouldBeTrue();
        listed.Select(static entry => entry.Id).ShouldBe([one, two]);
        reorder.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        badOrder.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        pinned.Select(static entry => entry.Id).ShouldBe([two, one]);
        remove.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        removeAgain.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Tags_CrudWithValidation()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();

        // Act
        var created = await EntriesApiTests.CreateTagAsync(context, "Работа", "#3366FF");
        var duplicate = await context.Client.PostAsJsonAsync(ApiRoutes.V1.Tags, new TagRequest { Name = "работа" }, DiskAppJsonContext.Default.TagRequest, TestContext.Current.CancellationToken);
        var badColor = await context.Client.PostAsJsonAsync(ApiRoutes.V1.Tags, new TagRequest { Name = "x", Color = "blue" }, DiskAppJsonContext.Default.TagRequest, TestContext.Current.CancellationToken);
        var update = await context.Client.PutAsJsonAsync(ApiRoutes.V1.Tag(created.Id), new TagRequest { Name = "Дом", Color = "#00AA00" }, DiskAppJsonContext.Default.TagRequest, TestContext.Current.CancellationToken);
        var all = await context.GetAsync(ApiRoutes.V1.Tags, DiskAppJsonContext.Default.IReadOnlyListTagDto);
        var delete = await context.Client.DeleteAsync(new Uri(ApiRoutes.V1.Tag(created.Id), UriKind.Relative), TestContext.Current.CancellationToken);
        var after = await context.GetAsync(ApiRoutes.V1.Tags, DiskAppJsonContext.Default.IReadOnlyListTagDto);

        // Assert
        created.Color.ShouldBe("#3366FF");
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        badColor.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        update.StatusCode.ShouldBe(HttpStatusCode.OK);
        all.ShouldHaveSingleItem().Name.ShouldBe("Дом");
        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        after.ShouldBeEmpty();
    }

    [Fact]
    public async Task Collections_ManualItemsAndSavedQuery()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        context.Files.CreateFile("a.pdf", "a");
        context.Files.CreateFile("b.pdf", "b");
        await context.AddRootAsync();
        var items = (await context.GetAsync(ApiRoutes.V1.Search + "?q=ext:pdf&sort=Name&desc=false", DiskAppJsonContext.Default.SearchResponse)).Items;

        // Act
        var manual = await CreateCollectionAsync(context, new CollectionRequest { Name = "Для отчёта", Kind = CollectionKind.Manual });
        var setItems = await context.Client.PutAsJsonAsync(ApiRoutes.V1.Collection(manual.Id) + "/items", new CollectionItemsRequest { EntryIds = [items[1].Id, items[0].Id] },
            DiskAppJsonContext.Default.CollectionItemsRequest, TestContext.Current.CancellationToken);
        var listed = await context.GetAsync(ApiRoutes.V1.Collection(manual.Id) + "/items", DiskAppJsonContext.Default.IReadOnlyListEntryDto);
        var saved = await CreateCollectionAsync(context, new CollectionRequest { Name = "PDF", Kind = CollectionKind.SavedQuery, Query = "ext:pdf" });
        var savedItems = await context.Client.GetAsync(new Uri(ApiRoutes.V1.Collection(saved.Id) + "/items", UriKind.Relative), TestContext.Current.CancellationToken);
        var noQuery = await context.Client.PostAsJsonAsync(ApiRoutes.V1.Collections, new CollectionRequest { Name = "Пусто", Kind = CollectionKind.SavedQuery },
            DiskAppJsonContext.Default.CollectionRequest, TestContext.Current.CancellationToken);
        var all = await context.GetAsync(ApiRoutes.V1.Collections, DiskAppJsonContext.Default.IReadOnlyListCollectionDto);
        var delete = await context.Client.DeleteAsync(new Uri(ApiRoutes.V1.Collection(manual.Id), UriKind.Relative), TestContext.Current.CancellationToken);

        // Assert
        setItems.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        listed.Select(static entry => entry.Name).ShouldBe(["b.pdf", "a.pdf"]);
        saved.Query.ShouldBe("ext:pdf");
        savedItems.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        noQuery.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        all.Count.ShouldBe(2);
        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    private static async Task<Contracts.Entries.EntryDto> PostFavoriteAsync(ServerTestContext context, long entryId)
    {
        var response = await context.Client.PostAsJsonAsync(ApiRoutes.V1.Favorites, new FavoriteRequest { EntryId = entryId }, DiskAppJsonContext.Default.FavoriteRequest, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(DiskAppJsonContext.Default.EntryDto, TestContext.Current.CancellationToken))!;
    }

    private static async Task<CollectionDto> CreateCollectionAsync(ServerTestContext context, CollectionRequest request)
    {
        var response = await context.Client.PostAsJsonAsync(ApiRoutes.V1.Collections, request, DiskAppJsonContext.Default.CollectionRequest, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync(DiskAppJsonContext.Default.CollectionDto, TestContext.Current.CancellationToken))!;
    }
}

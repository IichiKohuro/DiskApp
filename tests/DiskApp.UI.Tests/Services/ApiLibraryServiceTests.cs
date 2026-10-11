using DiskApp.Contracts.Entries;
using DiskApp.Contracts.Library;
using DiskApp.Contracts.Roots;
using DiskApp.Contracts.Search;
using DiskApp.UI.Models;
using DiskApp.UI.Services;

using ContractKind = DiskApp.Contracts.Entries.FileKind;

namespace DiskApp.UI.Tests.Services;

/// <summary>
/// Библиотека поверх клиента API (PLAN.md, этап 8.7): цвета тегов в записях, сохранённый запрос через поиск, поиск
/// записей по путям пачками, теги записи с сохранением подписи корня.
/// </summary>
public sealed class ApiLibraryServiceTests
{
    [Fact]
    public async Task GetFavorites_MapsTagColorsAndRootLabel()
    {
        // Arrange
        var (api, fake) = FakeDiskAppApi.Create();
        fake.Roots = [Root(1, "Документы")];
        fake.Handlers[nameof(api.GetFavoritesAsync)] = _ => Task.FromResult<IReadOnlyList<EntryDto>>(
            [Entry(5, "Отчёт.pdf") with { IsFavorite = true, Tags = [new TagDto(1, "работа", "#0078D4"), new TagDto(2, "черновик", null)] }]);
        var service = new ApiLibraryService(api);

        // Act
        var favorites = await service.GetFavoritesAsync(TestContext.Current.CancellationToken);

        // Assert
        var entry = favorites.ShouldHaveSingleItem();
        entry.Root.ShouldBe("Документы");
        entry.Tags.ShouldBe(["работа", "черновик"]);
        entry.TagLabels.ShouldBe([new TagLabel("работа", "#0078D4"), new TagLabel("черновик", null)]);
    }

    [Fact]
    public async Task GetCollectionItems_SavedQuery_SearchesItsQuery()
    {
        // Arrange
        var (api, fake) = FakeDiskAppApi.Create();
        fake.Search = _ => new SearchResponse([Entry(7, "a.pdf")], 1, false, null, 3, false, false, [], []);
        var service = new ApiLibraryService(api);

        // Act
        var items = await service.GetCollectionItemsAsync(new CollectionInfo(2, "Все PDF", CollectionType.SavedQuery, "ext:pdf"), TestContext.Current.CancellationToken);

        // Assert
        items.ShouldHaveSingleItem().Id.ShouldBe(7);
        fake.LastRequest!.Q.ShouldBe("ext:pdf");
        fake.LastRequest.Limit.ShouldBe(ApiLibraryService.SavedQueryLimit);
        fake.Calls.ShouldNotContain(nameof(api.GetCollectionItemsAsync));
    }

    [Fact]
    public async Task Resolve_ManyPaths_SendsChunksAndMergesWithoutDuplicates()
    {
        // Arrange
        var (api, fake) = FakeDiskAppApi.Create();
        var chunks = new List<int>();
        fake.Handlers[nameof(api.ResolveEntriesAsync)] = args =>
        {
            var paths = (IReadOnlyList<string>)args[0]!;
            chunks.Add(paths.Count);
            return Task.FromResult(new ResolveEntriesResponse([Entry(1, "same.txt")], [paths[^1]]));
        };
        var service = new ApiLibraryService(api);
        var paths = Enumerable.Range(0, ResolveEntriesRequest.MaxPaths + 5).Select(static index => $@"D:\Docs\f{index}.txt").ToArray();

        // Act
        var result = await service.ResolveAsync(paths, TestContext.Current.CancellationToken);

        // Assert
        chunks.ShouldBe([ResolveEntriesRequest.MaxPaths, 5]);
        result.Entries.ShouldHaveSingleItem().Id.ShouldBe(1);
        result.Missing.Count.ShouldBe(2);
    }

    [Fact]
    public async Task SetTags_KeepsRootAndUpdatesTags()
    {
        // Arrange
        var (api, fake) = FakeDiskAppApi.Create();
        IReadOnlyList<long>? sent = null;
        fake.Handlers[nameof(api.SetEntryTagsAsync)] = args =>
        {
            sent = (IReadOnlyList<long>)args[1]!;
            return Task.FromResult(Entry(9, "b.txt") with { Tags = [new TagDto(3, "дом", "#16C60C")] });
        };
        var service = new ApiLibraryService(api);
        var entry = new FileEntry { Id = 9, FullPath = @"D:\Docs\b.txt", Kind = UI.Models.FileKind.Text, Root = "Документы" };

        // Act
        var updated = await service.SetTagsAsync(entry, [3], TestContext.Current.CancellationToken);

        // Assert
        sent.ShouldBe([3L]);
        updated.Root.ShouldBe("Документы");
        updated.TagLabels.ShouldBe([new TagLabel("дом", "#16C60C")]);
    }

    [Fact]
    public async Task Collections_MapKindsAndSortByName()
    {
        // Arrange
        var (api, fake) = FakeDiskAppApi.Create();
        fake.Handlers[nameof(api.GetCollectionsAsync)] = _ => Task.FromResult<IReadOnlyList<CollectionDto>>(
            [new CollectionDto(2, "Яблоки", CollectionKind.SavedQuery, "яблоко"), new CollectionDto(1, "Архив", CollectionKind.Manual, null)]);
        var service = new ApiLibraryService(api);

        // Act
        var collections = await service.GetCollectionsAsync(TestContext.Current.CancellationToken);

        // Assert
        collections.ShouldBe([new CollectionInfo(1, "Архив", CollectionType.Manual, null), new CollectionInfo(2, "Яблоки", CollectionType.SavedQuery, "яблоко")]);
    }

    private static EntryDto Entry(long id, string name) => new()
    {
        Id = id,
        RootId = 1,
        Name = name,
        FullPath = @"D:\Docs\" + name,
        RelPath = name,
        Kind = ContractKind.Document,
        Size = 10,
        ModifiedUtc = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero),
    };

    private static RootDto Root(long id, string label)
        => new(id, @"D:\Docs", label, IsEnabled: true, IncludeHidden: false, IncludeSystem: false, MaxDepth: null, LastScanUtc: null,
            Status: RootStatus.Online, ErrorMessage: null);
}

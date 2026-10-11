using DiskApp.Client;
using DiskApp.Contracts.Entries;
using DiskApp.Contracts.Library;
using DiskApp.Contracts.Roots;
using DiskApp.Contracts.Search;
using DiskApp.UI.Models;
using DiskApp.UI.Services;

using ContractKind = DiskApp.Contracts.Entries.FileKind;
using FileKind = DiskApp.UI.Models.FileKind;

namespace DiskApp.UI.Tests.Services;

/// <summary>
/// Поиск окон поверх клиента API (PLAN.md, этап 7.1): перевод записей API в модель интерфейса, подписи корней,
/// подборка для пустого запроса, состояние индекса и открытие с запросом.
/// </summary>
public sealed class ApiSearchServiceTests
{
    private static readonly DateTimeOffset Modified = new(2026, 10, 1, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Search_Response_MapsEntriesRootLabelsAndApproximateTotal()
    {
        // Arrange
        var (api, fake) = FakeDiskAppApi.Create();
        fake.Roots = [Root(1, "Документы")];
        fake.Search = _ => Response([Entry(10, "report.pdf", ContractKind.Pdf) with { NameHighlights = [new TextRangeDto(0, 6)] }], total: 2000, approximate: true);
        var service = new ApiSearchService(api);

        // Act
        var result = await service.SearchAsync("report", TestContext.Current.CancellationToken);

        // Assert
        fake.LastRequest!.Q.ShouldBe("report");
        result.TotalCount.ShouldBe(2000);
        result.IsTotalApproximate.ShouldBeTrue();
        result.Elapsed.ShouldBe(TimeSpan.FromMilliseconds(12));
        var entry = result.Entries.ShouldHaveSingleItem();
        entry.Id.ShouldBe(10);
        entry.Name.ShouldBe("report.pdf");
        entry.Kind.ShouldBe(FileKind.Pdf);
        entry.Root.ShouldBe("Документы");
        entry.Tags.ShouldBe(["работа"]);
        entry.NameHighlights.ShouldBe([new HighlightRange(0, 6)]);
        entry.Modified.ShouldBe(Modified.ToLocalTime());
    }

    [Fact]
    public async Task Search_KnownRoots_DoesNotRequestRootsAgain()
    {
        // Arrange
        var (api, fake) = FakeDiskAppApi.Create();
        fake.Status = new IndexStatusDto(IndexerState.Idle, false, [RootStatus(1, "Документы")], TotalEntries: 5);
        fake.Search = _ => Response([Entry(1, "a.txt", ContractKind.Document)], total: 1, approximate: false);
        var service = new ApiSearchService(api);
        await service.GetIndexStatusAsync(TestContext.Current.CancellationToken);

        // Act
        var result = await service.SearchAsync("a", TestContext.Current.CancellationToken);

        // Assert
        result.Entries[0].Root.ShouldBe("Документы");
        fake.Calls.ShouldNotContain(nameof(IDiskAppApi.GetRootsAsync));
    }

    [Fact]
    public async Task GetRecent_OpenedFiles_ReturnsThemWithoutSearch()
    {
        // Arrange
        var (api, fake) = FakeDiskAppApi.Create();
        fake.Roots = [Root(1, "Документы")];
        fake.Recent = [Entry(5, "opened.txt", ContractKind.Document)];
        var service = new ApiSearchService(api);

        // Act
        var recent = await service.GetRecentAsync(TestContext.Current.CancellationToken);

        // Assert
        recent.ShouldHaveSingleItem().Name.ShouldBe("opened.txt");
        fake.Calls.ShouldNotContain(nameof(IDiskAppApi.SearchAsync));
    }

    [Fact]
    public async Task GetRecent_NothingOpenedYet_FallsBackToServerSelection()
    {
        // Arrange
        var (api, fake) = FakeDiskAppApi.Create();
        fake.Roots = [Root(1, "Документы")];
        fake.Search = _ => Response([Entry(3, "plan.docx", ContractKind.Document)], total: 1, approximate: false);
        var service = new ApiSearchService(api);

        // Act
        var recent = await service.GetRecentAsync(TestContext.Current.CancellationToken);

        // Assert
        fake.LastRequest!.Q.ShouldBe(string.Empty);
        fake.LastRequest.Limit.ShouldBe(20);
        recent.ShouldHaveSingleItem().Name.ShouldBe("plan.docx");
    }

    [Fact]
    public async Task GetSuggestions_Server_ReturnsRecentPinnedAndModifiedFilesInPlanOrder()
    {
        // Arrange
        var (api, fake) = FakeDiskAppApi.Create();
        fake.Roots = [Root(1, "Документы", offline: true)];
        fake.Recent = [Entry(1, "opened.txt", ContractKind.Document)];
        fake.Pinned = [Entry(2, "pinned.pdf", ContractKind.Pdf)];
        fake.Search = _ => Response(
            [Entry(3, "folder", ContractKind.Folder) with { IsDirectory = true }, Entry(4, "changed.xlsx", ContractKind.Spreadsheet)],
            total: 2,
            approximate: false);
        var service = new ApiSearchService(api);

        // Act
        var sections = await service.GetSuggestionsAsync(TestContext.Current.CancellationToken);

        // Assert
        sections.Select(static section => section.Title).ShouldBe([SectionTitles.RecentlyOpened, SectionTitles.Pinned, SectionTitles.RecentlyModified]);
        sections[2].Entries.ShouldHaveSingleItem().Name.ShouldBe("changed.xlsx", "в «Недавно изменённых» только файлы");
        sections[0].Entries[0].IsOffline.ShouldBeTrue("корень недоступен");
        fake.LastRequest!.Sort.ShouldBe(SearchSort.Modified);
        fake.LastRequest.Descending.ShouldBeTrue();
    }

    [Fact]
    public async Task GetSuggestions_EmptySections_AreSkipped()
    {
        // Arrange
        var (api, fake) = FakeDiskAppApi.Create();
        fake.Search = _ => Response([], total: 0, approximate: false);
        var service = new ApiSearchService(api);

        // Act
        var sections = await service.GetSuggestionsAsync(TestContext.Current.CancellationToken);

        // Assert
        sections.ShouldBeEmpty();
    }

    [Fact]
    public void ToEntry_CloudPlaceholder_IsCloudOnly()
    {
        // Act
        var entry = ApiSearchService.ToEntry(Entry(1, "video.mp4", ContractKind.Video) with { IsCloudPlaceholder = true }, "OneDrive");

        // Assert
        entry.IsCloudOnly.ShouldBeTrue();
        entry.IsOffline.ShouldBeFalse();
    }

    [Fact]
    public async Task GetIndexStatus_Indexing_ReportsTotalsRootsAndLatestScan()
    {
        // Arrange
        var (api, fake) = FakeDiskAppApi.Create();
        var latest = new DateTimeOffset(2026, 10, 11, 8, 0, 0, TimeSpan.Zero);
        fake.Status = new IndexStatusDto(IndexerState.Indexing, false,
            [RootStatus(1, "Документы", latest.AddHours(-3)) with { Progress = new IndexProgressDto(1, IndexPhase.Enumerating, 700, 650, null, 100, 7) }, RootStatus(2, "Проекты", latest)],
            TotalEntries: 1234);
        var service = new ApiSearchService(api);

        // Act
        var status = await service.GetIndexStatusAsync(TestContext.Current.CancellationToken);

        // Assert
        status.TotalEntries.ShouldBe(1234);
        status.IsIndexing.ShouldBeTrue();
        status.Progress.ShouldBeNull("объём первого сканирования неизвестен");
        status.Scanned.ShouldBe(700);
        status.DescribeIndexing().ShouldBe("индексация: просмотрено 700");
        status.Roots.ShouldBe(["Документы", "Проекты"]);
        status.LastUpdated.ShouldBe(latest);
    }

    [Fact]
    public async Task Open_Entry_PassesIdAndQueryToServer()
    {
        // Arrange
        var (api, fake) = FakeDiskAppApi.Create();
        var service = new ApiSearchService(api);
        var entry = ApiSearchService.ToEntry(Entry(42, "a.txt", ContractKind.Document), "Документы");

        // Act
        await service.OpenAsync(entry, "a", TestContext.Current.CancellationToken);

        // Assert
        fake.Opened.ShouldBe((42L, "a"));
    }

    [Fact]
    public async Task Suggest_ServerItems_MapKindsAndHistoryCalls()
    {
        // Arrange
        var (api, fake) = FakeDiskAppApi.Create();
        string? deleted = null;
        fake.Handlers[nameof(IDiskAppApi.SuggestAsync)] = _ => Task.FromResult(new SuggestResponse(
        [
            new SuggestionDto("ext:", "ext:", SuggestionKind.FilterKey),
            new SuggestionDto("ext:pdf", "ext:pdf", SuggestionKind.FilterValue),
            new SuggestionDto("отчёт", "отчёт", SuggestionKind.History),
        ]));
        fake.Handlers[nameof(IDiskAppApi.DeleteHistoryEntryAsync)] = args =>
        {
            deleted = (string)args[0]!;
            return Task.CompletedTask;
        };
        fake.Handlers[nameof(IDiskAppApi.ClearHistoryAsync)] = _ => Task.CompletedTask;
        var service = new ApiSearchService(api);
        var token = TestContext.Current.CancellationToken;

        // Act
        var suggestions = await service.SuggestAsync("ex", token);
        await service.DeleteHistoryAsync("отчёт", token);
        await service.ClearHistoryAsync(token);

        // Assert
        suggestions.Select(static item => item.Kind).ShouldBe([QuerySuggestionKind.FilterKey, QuerySuggestionKind.FilterValue, QuerySuggestionKind.History]);
        suggestions[2].IsHistory.ShouldBeTrue();
        deleted.ShouldBe("отчёт");
        fake.Calls.ShouldContain(nameof(IDiskAppApi.ClearHistoryAsync));
    }

    [Theory]
    [InlineData(ContractKind.Folder, FileKind.Folder)]
    [InlineData(ContractKind.Executable, FileKind.Installer)]
    [InlineData(ContractKind.Spreadsheet, FileKind.Spreadsheet)]
    [InlineData(ContractKind.Other, FileKind.Other)]
    public void ToEntry_Kind_MapsToInterfaceKind(ContractKind kind, FileKind expected)
    {
        // Act & Assert
        ApiSearchService.ToEntry(Entry(1, "x", kind), string.Empty).Kind.ShouldBe(expected);
    }

    private static EntryDto Entry(long id, string name, ContractKind kind) => new()
    {
        Id = id,
        RootId = 1,
        Name = name,
        FullPath = @"D:\Docs\" + name,
        RelPath = name,
        Kind = kind,
        Size = 1024,
        ModifiedUtc = Modified,
        IconKey = "ext:" + Path.GetExtension(name).TrimStart('.'),
        Tags = [new TagDto(1, "работа", null)],
    };

    private static RootDto Root(long id, string label, bool offline = false)
        => new(id, @"D:\Docs", label, IsEnabled: true, IncludeHidden: false, IncludeSystem: false, MaxDepth: null, LastScanUtc: null,
            Status: offline ? Contracts.Roots.RootStatus.Offline : Contracts.Roots.RootStatus.Online, ErrorMessage: null);

    private static RootIndexStatusDto RootStatus(long id, string label, DateTimeOffset? lastScan = null)
        => new(id, @"D:\Docs", label, Contracts.Roots.RootStatus.Online, Message: null, RunningTask: null, IsQueued: false, Progress: null, LastScanUtc: lastScan);

    private static SearchResponse Response(IReadOnlyList<EntryDto> items, long total, bool approximate)
        => new(items, total, approximate, NextCursor: null, ElapsedMs: 12, IsPartial: false, IncludesFuzzy: false, Warnings: [], Tokens: []);
}

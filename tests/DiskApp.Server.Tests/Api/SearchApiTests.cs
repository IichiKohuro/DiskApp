using System.Net;
using System.Net.Http.Json;

using DiskApp.Contracts;
using DiskApp.Contracts.Entries;
using DiskApp.Contracts.Search;

namespace DiskApp.Server.Tests.Api;

/// <summary>
/// Поиск через API: подсветка, курсоры, GET и POST, проверка параметров, подсказки и история.
/// </summary>
public sealed class SearchApiTests
{
    [Fact]
    public async Task Get_Query_ReturnsHighlightsTokensAndPagesWithCursor()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        for (var i = 0; i < 25; i++)
        {
            context.Files.CreateFile(Path.Combine("reports", $"report-{i:00}.txt"), "r");
        }

        await context.AddRootAsync();

        // Act
        var first = await context.GetAsync(ApiRoutes.V1.Search + "?q=report%20ext:txt&limit=10", DiskAppJsonContext.Default.SearchResponse);
        var second = await context.GetAsync(ApiRoutes.V1.Search + "?q=report%20ext:txt&limit=10&cursor=" + first.NextCursor, DiskAppJsonContext.Default.SearchResponse);

        // Assert
        first.Items.Count.ShouldBe(10);
        first.Total.ShouldBe(25);
        first.NextCursor.ShouldNotBeNullOrEmpty();
        var hit = first.Items[0];
        hit.Score.ShouldNotBeNull();
        hit.Match.ShouldBe(MatchKind.Prefix);
        hit.NameHighlights.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(new TextRangeDto(0, 6));
        hit.FullPath.ShouldEndWith(Path.Combine("reports", hit.Name));
        hit.IconKey.ShouldBe("ext:txt");
        first.Tokens.Select(static token => token.Kind).ShouldBe([QueryTokenKind.Term, QueryTokenKind.Filter]);
        second.Items.Count.ShouldBe(10);
        second.Items.Select(static item => item.Id).Intersect(first.Items.Select(static item => item.Id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Get_RightAfterAddingRoot_ReturnsFullPathsOfItsEntries()
    {
        // Arrange: поиск заполняет кэш корней, потом добавляется второй корень.
        await using var context = await ServerTestContext.StartAsync();
        context.Files.CreateFile(Path.Combine("first", "alpha-note.txt"), "a");
        context.Files.CreateFile(Path.Combine("second", "alpha-plan.txt"), "b");
        await context.AddRootAsync("first");
        await context.GetAsync(ApiRoutes.V1.Search + "?q=alpha", DiskAppJsonContext.Default.SearchResponse);

        // Act
        var second = await context.AddRootAsync("second");
        var page = await context.GetAsync(ApiRoutes.V1.Search + "?q=alpha", DiskAppJsonContext.Default.SearchResponse);

        // Assert
        var plan = page.Items.Single(static item => item.Name == "alpha-plan.txt");
        plan.FullPath.ShouldBe(Path.Combine(second.Path, "alpha-plan.txt"), "кэш корней поиска сброшен при добавлении корня");
    }

    [Fact]
    public async Task Post_SameRequest_MatchesGet()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        context.Files.CreateFile("invoice-2026.pdf", "i");
        context.Files.CreateFile("notes.txt", "n");
        await context.AddRootAsync();

        // Act
        var response = await context.Client.PostAsJsonAsync(ApiRoutes.V1.Search, new SearchRequest { Q = "invoice", Explain = true },
            DiskAppJsonContext.Default.SearchRequest, TestContext.Current.CancellationToken);
        var page = await response.Content.ReadFromJsonAsync(DiskAppJsonContext.Default.SearchResponse, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var item = page!.Items.ShouldHaveSingleItem();
        item.Name.ShouldBe("invoice-2026.pdf");
        item.Kind.ShouldBe(FileKind.Pdf);
        item.RankReason.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task QueryOverLimit_Returns400ForSearchAndSuggest()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var text = string.Join(' ', Enumerable.Repeat("word", SearchRequest.MaxQueryLength / 4));

        // Act
        var search = await context.Client.PostAsJsonAsync(ApiRoutes.V1.Search, new SearchRequest { Q = text },
            DiskAppJsonContext.Default.SearchRequest, TestContext.Current.CancellationToken);
        var suggest = await context.Client.GetAsync(new Uri(ApiRoutes.V1.Suggest + "?q=" + Uri.EscapeDataString(text), UriKind.Relative), TestContext.Current.CancellationToken);
        var atLimit = await context.Client.PostAsJsonAsync(ApiRoutes.V1.Search, new SearchRequest { Q = new string('a', SearchRequest.MaxQueryLength) },
            DiskAppJsonContext.Default.SearchRequest, TestContext.Current.CancellationToken);

        // Assert
        text.Length.ShouldBeGreaterThan(SearchRequest.MaxQueryLength);
        search.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await search.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("\"q\"");
        suggest.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        atLimit.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("?q=a&limit=0")]
    [InlineData("?q=a&limit=501")]
    [InlineData("?q=a&cursor=not-a-cursor")]
    public async Task Get_InvalidParameters_Return400(string query)
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();

        // Act
        var response = await context.Client.GetAsync(new Uri(ApiRoutes.V1.Search + query, UriKind.Relative), TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Get_SortByNameAscending_OrdersAlphabetically()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        context.Files.CreateFile("b-data.csv", "b");
        context.Files.CreateFile("a-data.csv", "a");
        context.Files.CreateFile("c-data.csv", "c");
        await context.AddRootAsync();

        // Act
        var page = await context.GetAsync(ApiRoutes.V1.Search + "?q=ext:csv&sort=Name&desc=false", DiskAppJsonContext.Default.SearchResponse);

        // Assert
        page.Items.Select(static item => item.Name).ShouldBe(["a-data.csv", "b-data.csv", "c-data.csv"]);
    }

    [Fact]
    public async Task SuggestAndHistory_RecordListDeleteAndClear()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        context.Files.CreateFile("budget.xlsx", "b");
        await context.AddRootAsync();
        var entry = (await context.GetAsync(ApiRoutes.V1.Search + "?q=budget", DiskAppJsonContext.Default.SearchResponse)).Items.ShouldHaveSingleItem();
        await context.Client.PostAsync(new Uri(ApiRoutes.V1.Entry(entry.Id) + "/open?query=budget", UriKind.Relative), null, TestContext.Current.CancellationToken);
        await context.Client.PostAsync(new Uri(ApiRoutes.V1.Entry(entry.Id) + "/open?query=bud%20ext:xlsx", UriKind.Relative), null, TestContext.Current.CancellationToken);

        // Act
        var history = await context.GetAsync(ApiRoutes.V1.History, DiskAppJsonContext.Default.IReadOnlyListHistoryEntryDto);
        var suggest = await context.GetAsync(ApiRoutes.V1.Suggest + "?q=ext:", DiskAppJsonContext.Default.SuggestResponse);
        var delete = await context.Client.DeleteAsync(new Uri(ApiRoutes.V1.HistoryEntry + "?query=budget", UriKind.Relative), TestContext.Current.CancellationToken);
        var deleteMissing = await context.Client.DeleteAsync(new Uri(ApiRoutes.V1.HistoryEntry + "?query=budget", UriKind.Relative), TestContext.Current.CancellationToken);
        var afterDelete = await context.GetAsync(ApiRoutes.V1.History, DiskAppJsonContext.Default.IReadOnlyListHistoryEntryDto);
        var clear = await context.Client.DeleteAsync(new Uri(ApiRoutes.V1.History, UriKind.Relative), TestContext.Current.CancellationToken);
        var afterClear = await context.GetAsync(ApiRoutes.V1.History, DiskAppJsonContext.Default.IReadOnlyListHistoryEntryDto);

        // Assert
        history.Select(static item => item.Query).ShouldBe(["bud ext:xlsx", "budget"], ignoreOrder: true);
        suggest.Items.ShouldContain(item => item.Kind == SuggestionKind.FilterValue && item.Query == "ext:xlsx");
        delete.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        deleteMissing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        afterDelete.ShouldHaveSingleItem().Query.ShouldBe("bud ext:xlsx");
        clear.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        afterClear.ShouldBeEmpty();
    }
}

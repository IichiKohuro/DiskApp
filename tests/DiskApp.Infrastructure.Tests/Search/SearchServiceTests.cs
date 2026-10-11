using System.Text;

using DiskApp.Core.Search;

using static DiskApp.Infrastructure.Tests.Search.SearchTestContext;

namespace DiskApp.Infrastructure.Tests.Search;

/// <summary>
/// Поиск по настоящей базе: стратегии термов, ё/е, отрицания, фразы, все фильтры, подборка для пустого запроса,
/// страницы, усечение с добором лучших, нечёткие совпадения, кэш уточнения, отмена и таймаут, защита от инъекций,
/// подсказки и история.
/// </summary>
public sealed class SearchServiceTests
{
    [Fact]
    public async Task Search_Term_FindsSubstringsExactFirst()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.AddAsync(context.Projects, "docs/myreports.docx", "docs/Quarterly report.docx", "docs/report.docx", "docs/report-final.docx", "docs/notes.txt");

        // Act
        var page = await Search(context, "report");

        // Assert
        Names(page).ShouldBe(["report.docx", "report-final.docx", "Quarterly report.docx", "myreports.docx"]);
        page.TotalCount.ShouldBe(4);
        page.Hits[0].Match.ShouldBe(MatchKind.Exact);
        page.Hits[0].FullPath.ShouldEndWith(Path.Combine("docs", "report.docx"));
        page.Hits[0].NameHighlights.ShouldBe([new TextRange(0, 6)]);
        page.ParsedQuery!.Terms.ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData("отчет")]
    [InlineData("ОТЧЁТ")]
    [InlineData("Отчёт")]
    public async Task Search_YoAndCase_Insensitive(string text)
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.AddAsync(context.Projects, "Отчёт за квартал.docx", "отчеты 2025.xlsx", "план.txt");

        // Act
        var page = await Search(context, text);

        // Assert
        Names(page).ShouldBe(["Отчёт за квартал.docx", "отчеты 2025.xlsx"], ignoreOrder: true);
    }

    [Theory]
    [InlineData("caf\u00E9")]
    [InlineData("cafe\u0301")]
    [InlineData("йога")]
    [InlineData("и\u0306ога")]
    public async Task Search_ComposedAndDecomposedForms_FindEachOther(string text)
    {
        // Arrange: имена с macOS и из архивов часто приходят в форме NFD, запрос с клавиатуры — в NFC
        await using var context = await CreateAsync();
        await context.AddAsync(context.Projects, "cafe\u0301 menu.pdf", "caf\u00E9 bill.pdf", "и\u0306ога дом.txt", "йога зал.txt", "other.txt");

        // Act
        var page = await Search(context, text);

        // Assert
        var expected = text.Normalize(NormalizationForm.FormC).StartsWith('c')
            ? new[] { "cafe\u0301 menu.pdf", "caf\u00E9 bill.pdf" }
            : ["и\u0306ога дом.txt", "йога зал.txt"];
        Names(page).ShouldBe(expected, ignoreOrder: true);
    }

    [Fact]
    public async Task Search_ShortTerm_IsNamePrefix()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.AddAsync(context.Projects, "readme.md", "Report.pdf", "future.txt", "RE-plan.txt");

        // Act
        var page = await Search(context, "re");

        // Assert
        Names(page).ShouldBe(["readme.md", "Report.pdf", "RE-plan.txt"], ignoreOrder: true);
    }

    [Fact]
    public async Task Search_LongAndShortTerms_BothRequired()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.AddAsync(context.Projects, "Q3 report.xlsx", "Q4 report.xlsx", "report.docx");

        // Act
        var page = await Search(context, "report q3");

        // Assert
        Names(page).ShouldBe(["Q3 report.xlsx"]);
    }

    [Fact]
    public async Task Search_NegationAndPhrase_FilterNames()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.AddAsync(context.Projects, "Annual Report 2026.pdf", "report annual.txt", "annual report draft.docx");

        // Act
        var phrase = await Search(context, "\"annual report\"");
        var negated = await Search(context, "\"annual report\" -draft");

        // Assert
        Names(phrase).ShouldBe(["Annual Report 2026.pdf", "annual report draft.docx"], ignoreOrder: true);
        Names(negated).ShouldBe(["Annual Report 2026.pdf"]);
    }

    [Fact]
    public async Task Search_ExtensionKindAndAttributeFilters()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.AddAsync(context.Projects,
            "work/plan.pdf", "work/plan.docx", "work/plan.tmp", "work/photo.jpg",
            new TestFile("work/hidden plan.pdf", Attributes: FileAttributes.Hidden),
            new TestFile("work/plans", IsDirectory: true));

        // Act & Assert
        Names(await Search(context, "plan ext:pdf")).ShouldBe(["plan.pdf", "hidden plan.pdf"], ignoreOrder: true);
        Names(await Search(context, "plan ext:pdf,docx -is:hidden")).ShouldBe(["plan.pdf", "plan.docx"], ignoreOrder: true);
        Names(await Search(context, "plan -ext:tmp -ext:pdf is:file")).ShouldBe(["plan.docx"]);
        Names(await Search(context, "type:картинки")).ShouldBe(["photo.jpg"]);
        Names(await Search(context, "plan is:dir")).ShouldBe(["plans"]);
        Names(await Search(context, "plan is:hidden")).ShouldBe(["hidden plan.pdf"]);
    }

    [Fact]
    public async Task Search_SizeAndDateFilters()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.AddAsync(context.Projects,
            new TestFile("big.iso", Size: 5L * 1024 * 1024 * 1024, Modified: Now.AddDays(-2)),
            new TestFile("medium.zip", Size: 50L * 1024 * 1024, Modified: Now.AddDays(-40)),
            new TestFile("small.txt", Size: 10, Modified: Now.AddHours(-3), Created: Now.AddHours(-3)),
            new TestFile("empty.txt", Size: 0, Modified: new DateTimeOffset(2025, 3, 15, 10, 0, 0, TimeSpan.Zero)));

        // Act & Assert
        Names(await Search(context, "size:>1gb")).ShouldBe(["big.iso"]);
        Names(await Search(context, "size:10mb..100mb")).ShouldBe(["medium.zip"]);
        Names(await Search(context, "size:empty")).ShouldBe(["empty.txt"]);
        Names(await Search(context, "modified:week is:file")).ShouldBe(["small.txt", "big.iso"], ignoreOrder: true);
        Names(await Search(context, "modified:today")).ShouldBe(["small.txt"]);
        Names(await Search(context, "modified:2025-03")).ShouldBe(["empty.txt"]);
        Names(await Search(context, "created:today")).ShouldBe(["small.txt"]);
        Names(await Search(context, "is:file -modified:month")).ShouldBe(["medium.zip", "empty.txt"], ignoreOrder: true);
    }

    [Fact]
    public async Task Search_FolderRootTagAndFavoriteFilters()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.AddAsync(context.Projects, "DiskApp/src/main.cs", "DiskApp/docs/main.md", "Other/main.cs", "Archive/src/main.cs");
        await context.AddAsync(context.Photos, "2026/main.jpg");
        await context.TagAsync(context.Projects, "DiskApp/src/main.cs", "Работа");
        await context.FavoriteAsync(context.Projects, "Other/main.cs");
        var rootSeparator = Path.DirectorySeparatorChar;

        // Act & Assert
        Names(await Search(context, "main in:src")).ShouldBe(["main.cs", "main.cs"]);
        (await Search(context, "main in:src")).Hits.ShouldAllBe(hit => hit.FullPath.Contains($"{rootSeparator}src{rootSeparator}"));
        Names(await Search(context, "main in:DiskApp -in:docs")).ShouldBe(["main.cs"]);
        Names(await Search(context, $"main in:{Path.Combine(context.Projects.Path, "DiskApp")}")).ShouldBe(["main.cs", "main.md"], ignoreOrder: true);
        Names(await Search(context, "main in:Photos")).ShouldBe(["main.jpg"]);
        Names(await Search(context, "main root:photos")).ShouldBe(["main.jpg"]);
        (await Search(context, "main -root:photos")).Hits.Count.ShouldBe(4);
        Names(await Search(context, "main tag:работа")).ShouldBe(["main.cs"]);
        (await Search(context, "main -tag:работа")).Hits.Count.ShouldBe(4);
        (await Search(context, "main fav:yes")).Hits.ShouldHaveSingleItem().IsFavorite.ShouldBeTrue();
        (await Search(context, "main in:Nowhere")).Hits.ShouldBeEmpty();
        var unknownRoot = await Search(context, "main root:Nowhere");
        unknownRoot.Hits.ShouldBeEmpty();
        unknownRoot.Warnings.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Search_Regex_FiltersNames()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.AddAsync(context.Projects, "inv-2026-01.pdf", "inv-2026-02.pdf", "invoice.docx", "invite.pdf");

        // Act
        var page = await Search(context, "/^inv-\\d{4}-\\d{2}\\.pdf$/");

        // Assert
        Names(page).ShouldBe(["inv-2026-01.pdf", "inv-2026-02.pdf"], ignoreOrder: true);
    }

    [Fact]
    public async Task Search_EmptyQuery_FavoritesAndRecentFirstThenRecentlyModified()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.AddAsync(context.Projects,
            new TestFile("old.txt", Modified: Now.AddYears(-2)),
            new TestFile("fresh.txt", Modified: Now.AddMinutes(-5)),
            "used.docx",
            "pinned.pdf");
        await context.OpenAsync(context.Projects, "used.docx", 5);
        await context.FavoriteAsync(context.Projects, "pinned.pdf");

        // Act
        var page = await Search(context, string.Empty);

        // Assert
        Names(page).Take(3).ShouldBe(["pinned.pdf", "used.docx", "fresh.txt"], ignoreOrder: true);
        page.Hits.Select(static hit => hit.Entry.Name).ShouldContain("old.txt");
        page.Hits.First(static hit => hit.Entry.Name == "used.docx").OpenCount.ShouldBe(5);
    }

    [Fact]
    public async Task Search_Paging_CoversAllWithoutDuplicates()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.AddAsync(context.Projects, Enumerable.Range(0, 23).Select(static i => (TestFile)$"files/data{i:00}.csv").ToArray());
        var service = context.Service();
        var seen = new List<string>();
        SearchCursor? cursor = null;

        // Act
        do
        {
            var page = await service.SearchAsync(new SearchRequest { Text = "data", Limit = 10, Cursor = cursor }, TestContext.Current.CancellationToken);
            seen.AddRange(page.Hits.Select(static hit => hit.Entry.Name));
            page.TotalCount.ShouldBe(23);
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        // Assert
        seen.Count.ShouldBe(23);
        seen.Distinct().Count().ShouldBe(23);
    }

    [Fact]
    public async Task Search_MoreMatchesThanLimit_CountsAllAndBoostsBestMatches()
    {
        // Arrange: точное совпадение вставлено последним — без добора оно не попало бы в первые 10 по rowid
        await using var context = await CreateAsync();
        await context.AddAsync(context.Projects, Enumerable.Range(0, 60).Select(static i => (TestFile)$"logs/old-report-{i:00}.log").ToArray());
        await context.AddAsync(context.Projects, "report.txt", "used-report.log");
        await context.OpenAsync(context.Projects, "used-report.log", 10);
        var service = context.Service(new SearchOptions { CandidateLimit = 10 });

        // Act
        var page = await service.SearchAsync(new SearchRequest { Text = "report", Limit = 5 }, TestContext.Current.CancellationToken);

        // Assert
        page.TotalCount.ShouldBe(62);
        page.IsTotalApproximate.ShouldBeFalse();
        Names(page).Take(2).ShouldBe(["report.txt", "used-report.log"]);
    }

    [Fact]
    public async Task Search_Typo_AddsFuzzyMatches()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.AddAsync(context.Projects, "report.docx", "repository.zip", "unrelated.txt");

        // Act
        var page = await Search(context, "reprot");

        // Assert
        page.IncludesFuzzy.ShouldBeTrue();
        page.IsTotalApproximate.ShouldBeTrue();
        Names(page).First().ShouldBe("report.docx");
        Names(page).ShouldNotContain("unrelated.txt");
    }

    [Fact]
    public async Task Search_FuzzyTurnedOffInSettings_AppliesWithoutRestart()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.AddAsync(context.Projects, "report.docx", "unrelated.txt");
        var service = context.Service();
        var before = await service.SearchAsync(new SearchRequest { Text = "reprot", Limit = 5 }, TestContext.Current.CancellationToken);

        // Act
        service.UpdateOptions(SearchOptions.FromSettings(new DiskApp.Core.Settings.SearchSettings { Fuzzy = false }));
        var after = await service.SearchAsync(new SearchRequest { Text = "reprot", Limit = 5 }, TestContext.Current.CancellationToken);

        // Assert
        before.IncludesFuzzy.ShouldBeTrue();
        after.IncludesFuzzy.ShouldBeFalse();
        Names(after).ShouldBeEmpty();
    }

    [Fact]
    public async Task Search_RefinedQuery_ReusesCandidatesWithoutIndexCall()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.AddAsync(context.Projects, "report.docx", "repository.zip", "reptile.png");
        var counting = new CountingIndex(context.Index);
        var service = context.Service(index: counting);

        // Act
        var first = await service.SearchAsync(new SearchRequest { Text = "rep" }, TestContext.Current.CancellationToken);
        var refined = await service.SearchAsync(new SearchRequest { Text = "repo" }, TestContext.Current.CancellationToken);
        var refinedMore = await service.SearchAsync(new SearchRequest { Text = "report" }, TestContext.Current.CancellationToken);
        var otherFilter = await service.SearchAsync(new SearchRequest { Text = "report ext:docx" }, TestContext.Current.CancellationToken);

        // Assert
        Names(first).Count.ShouldBe(3);
        Names(refined).ShouldBe(["report.docx", "repository.zip"], ignoreOrder: true);
        Names(refinedMore).ShouldBe(["report.docx"]);
        Names(otherFilter).ShouldBe(["report.docx"]);
        counting.FindCalls.ShouldBe(2, "уточнения взяты из кэша, новый фильтр — снова из индекса");
    }

    [Fact]
    public async Task Search_Cancelled_Throws()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.AddAsync(context.Projects, "report.docx");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // Act & Assert
        await Should.ThrowAsync<OperationCanceledException>(
            () => context.Service().SearchAsync(new SearchRequest { Text = "report" }, cancellation.Token));
    }

    [Fact]
    public async Task Search_SlowQueryPastTimeout_ReturnsPartialResult()
    {
        // Arrange: регулярное выражение вызывает .NET для каждой строки — 20 тыс. строк не уложатся в 1 мс
        await using var context = await CreateAsync();
        await context.AddAsync(context.Projects, Enumerable.Range(0, 20_000).Select(static i => (TestFile)$"bulk/item{i:00000}.dat").ToArray());
        var service = context.Service(new SearchOptions { QueryTimeout = TimeSpan.FromMilliseconds(1) });

        // Act
        var page = await service.SearchAsync(new SearchRequest { Text = "/item\\d+5\\.dat$/" }, TestContext.Current.CancellationToken);

        // Assert
        page.IsPartial.ShouldBeTrue();
        page.IsTotalApproximate.ShouldBeTrue();
    }

    [Theory]
    [InlineData("'); DROP TABLE entries; --")]
    [InlineData("\" OR 1=1 --")]
    [InlineData("ext:pdf') OR 1=1 --")]
    [InlineData("in:x') OR ('1'='1")]
    [InlineData("tag:a' OR 'a'='a")]
    [InlineData("NOT report*")]
    [InlineData("report:^ NEAR(a b)")]
    public async Task Search_HostileInput_TreatedAsDataAndTablesIntact(string text)
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.AddAsync(context.Projects, "report.pdf", "secret.txt");

        // Act
        var page = await Search(context, text);

        // Assert
        page.Hits.Select(static hit => hit.Entry.Name).ShouldNotContain("secret.txt");
        (await context.Entries.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(2);
    }

    [Fact]
    public async Task Suggest_KeysValuesAndHistory()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.AddAsync(context.Projects, "Documents/a.pdf", "Documents/b.pdf", "Downloads/c.png", "x.psd");
        await context.TagAsync(context.Projects, "x.psd", "Работа");
        var service = context.Service();
        await service.RecordQueryAsync("отчет ext:pdf", TestContext.Current.CancellationToken);

        // Act
        var keys = await service.SuggestAsync("отчет ex", cancellationToken: TestContext.Current.CancellationToken);
        var extensions = await service.SuggestAsync("ext:p", cancellationToken: TestContext.Current.CancellationToken);
        var tags = await service.SuggestAsync("-tag:раб", cancellationToken: TestContext.Current.CancellationToken);
        var folders = await service.SuggestAsync("in:do", cancellationToken: TestContext.Current.CancellationToken);
        var kinds = await service.SuggestAsync("type:карт", cancellationToken: TestContext.Current.CancellationToken);
        var history = await service.SuggestAsync("отч", cancellationToken: TestContext.Current.CancellationToken);
        var empty = await service.SuggestAsync(string.Empty, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        keys.Select(static s => s.Query).ShouldContain("отчет ext:");
        extensions.Select(static s => s.Query).ShouldBe(["ext:pdf", "ext:png", "ext:psd"]);
        tags.Select(static s => s.Query).ShouldBe(["-tag:Работа"]);
        folders.Select(static s => s.Query).ShouldBe(["in:Documents", "in:Downloads"], ignoreOrder: true);
        kinds.Select(static s => s.Query).ShouldContain("type:картинки");
        history.ShouldContain(static s => s.Kind == SuggestionKind.History && s.Query == "отчет ext:pdf");
        empty.ShouldHaveSingleItem().Query.ShouldBe("отчет ext:pdf");
    }

    [Fact]
    public async Task RecordQuery_BeyondLimit_PrunesOldest()
    {
        // Arrange
        await using var context = await CreateAsync();
        var service = context.Service(new SearchOptions { HistoryLimit = 3 });

        // Act
        for (var i = 0; i < 5; i++)
        {
            context.Clock.UtcNow = Now.AddMinutes(i);
            await service.RecordQueryAsync($"query {i}", TestContext.Current.CancellationToken);
        }

        await service.RecordQueryAsync("   ", TestContext.Current.CancellationToken);
        var history = await service.GetHistoryAsync(10, TestContext.Current.CancellationToken);
        var removed = await service.RemoveFromHistoryAsync("query 4", TestContext.Current.CancellationToken);
        await service.ClearHistoryAsync(TestContext.Current.CancellationToken);

        // Assert
        history.Select(static entry => entry.Query).ShouldBe(["query 4", "query 3", "query 2"]);
        removed.ShouldBeTrue();
        (await service.GetHistoryAsync(10, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Search_SortByName_OrdersAlphabetically()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.AddAsync(context.Projects, "c-report.txt", "a-report.txt", "b-report.txt");

        // Act
        var page = await context.Service().SearchAsync(new SearchRequest { Text = "report", Sort = SearchSortField.Name, Descending = false }, TestContext.Current.CancellationToken);

        // Assert
        Names(page).ShouldBe(["a-report.txt", "b-report.txt", "c-report.txt"]);
    }

    private static Task<SearchPage> Search(SearchTestContext context, string text)
        => context.Service().SearchAsync(new SearchRequest { Text = text, IncludeRankReason = true }, TestContext.Current.CancellationToken);

    private static List<string> Names(SearchPage page) => page.Hits.Select(static hit => hit.Entry.Name).ToList();

    private sealed class CountingIndex(ISearchIndex inner) : ISearchIndex
    {
        public int FindCalls { get; private set; }

        public Task<CandidateBatch> FindAsync(SearchPlan plan, CandidateQuery query, CancellationToken cancellationToken = default)
        {
            FindCalls++;
            return inner.FindAsync(plan, query, cancellationToken);
        }

        public Task<CandidateCount> CountAsync(SearchPlan plan, long cap, CancellationToken cancellationToken = default) => inner.CountAsync(plan, cap, cancellationToken);

        public Task<IReadOnlyList<SearchCandidate>> FindRecentAsync(int limit, CancellationToken cancellationToken = default) => inner.FindRecentAsync(limit, cancellationToken);

        public Task<IReadOnlyList<SearchCandidate>> FindByAnyTrigramAsync(SearchPlan plan, IReadOnlyList<string> trigrams, int limit, CancellationToken cancellationToken = default)
            => inner.FindByAnyTrigramAsync(plan, trigrams, limit, cancellationToken);

        public Task<IReadOnlyList<string>> GetTopExtensionsAsync(string prefix, int limit, CancellationToken cancellationToken = default)
            => inner.GetTopExtensionsAsync(prefix, limit, cancellationToken);

        public Task<IReadOnlyList<string>> FindFolderNamesAsync(string prefix, int limit, CancellationToken cancellationToken = default)
            => inner.FindFolderNamesAsync(prefix, limit, cancellationToken);
    }
}

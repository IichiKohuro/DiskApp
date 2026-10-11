using DiskApp.Core.Search;

using static DiskApp.Infrastructure.Tests.Search.SearchTestContext;

namespace DiskApp.Infrastructure.Tests.Search;

/// <summary>
/// Golden-тесты ранжирования (PLAN.md, этап 3.3): фиксированный корпус из ~500 записей — проекты, финансы, документы,
/// загрузки, фото и шум (журналы, кэш, снимки камеры) — со статистикой открытий и избранным; для 32 запросов закреплено,
/// что окажется наверху. Изменение весов или стратегии, ломающее ожидаемый порядок, видно сразу.
/// </summary>
public sealed class SearchGoldenTests : IAsyncLifetime
{
    private SearchTestContext? _context;
    private SearchService? _service;

    /// <summary>Запрос → ожидаемые первые результаты по порядку.</summary>
    public static TheoryData<string, string[]> TopInOrder { get; } = new()
    {
        { "readme", ["README.md"] },
        { "plan", ["PLAN.md"] },
        { "searchservice", ["SearchService.cs"] },
        { "search", ["SearchService.cs"] },
        { "бюджет", ["Бюджет 2026.xlsx", "Бюджет 2025.xlsx"] },
        { "бюджет 2025", ["Бюджет 2025.xlsx"] },
        { "отчет q3", ["Отчёт за квартал Q3.docx"] },
        { "квартал", ["Квартальный обзор.pptx"] },
        { "invoice", ["invoice-2026-04.pdf", "invoice-2026-03.pdf"] },
        { "счет", ["Счёт-фактура 0412.pdf"] },
        { "report", ["report (2).pdf", "report (1).pdf"] },
        { "setup", ["setup-vscode-x64.exe"] },
        { "vscode", ["setup-vscode-x64.exe"] },
        { "море", ["море 001.jpg"] },
        { "arch", ["ARCHITECTURE.md"] },
        { "договор", ["Договор аренды.pdf"] },
        { "паспорт", ["Паспорт скан.pdf"] },
        { "резюме", ["Резюме.docx"] },
        { "стратегия", ["Стратегия 2027.pptx"] },
        { "q3", ["Q3 summary.txt"] },
        { "mainview", ["MainView.axaml"] },
        { "queryparser", ["QueryParser.cs"] },
        { "is:dir src", ["src"] },
        { "node", ["node-v20.msi"] },
    };

    /// <summary>Запрос → сколько первых смотреть → что обязано там быть.</summary>
    public static TheoryData<string, int, string[]> WithinTop { get; } = new()
    {
        { "отчет", 2, ["Отчёт за квартал Q3.docx", "Отчёт за квартал Q2.docx"] },
        { "reprot", 3, ["report (2).pdf", "report (1).pdf"] },
        { "ext:pdf invoice", 2, ["invoice-2026-04.pdf", "invoice-2026-03.pdf"] },
        { "type:presentation", 2, ["Стратегия 2027.pptx", "Квартальный обзор.pptx"] },
        { "type:audio", 3, ["Lullaby.mp3", "Morning.mp3", "Night drive.flac"] },
        { "квартал -обзор", 2, ["Отчёт за квартал Q3.docx", "Отчёт за квартал Q2.docx"] },
        { "plan", 3, ["PLAN.md", "plan-2026.docx"] },
        { "modified:today", 2, ["Q3 summary.txt", "SearchService.cs"] },
    };

    public async ValueTask InitializeAsync()
    {
        _context = await CreateAsync();
        await SeedAsync(_context);
        // Эталонам нужен полный результат: под нагрузкой параллельных тестов предел запроса 500 мс давал частичный счёт.
        _service = _context.Service(new SearchOptions { QueryTimeout = TimeSpan.FromSeconds(30) });
    }

    public async ValueTask DisposeAsync()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
    }

    [Theory]
    [MemberData(nameof(TopInOrder))]
    public async Task Search_GoldenQuery_TopResultsInOrder(string query, string[] expected)
    {
        // Act
        var page = await _service!.SearchAsync(new SearchRequest { Text = query, Limit = 10, IncludeRankReason = true }, TestContext.Current.CancellationToken);

        // Assert
        var names = page.Hits.Select(static hit => hit.Entry.Name).ToArray();
        names.Take(expected.Length).ShouldBe(expected, $"«{query}»: {Describe(page)}");
    }

    [Theory]
    [MemberData(nameof(WithinTop))]
    public async Task Search_GoldenQuery_ExpectedWithinTop(string query, int top, string[] expected)
    {
        // Act
        var page = await _service!.SearchAsync(new SearchRequest { Text = query, Limit = 10, IncludeRankReason = true }, TestContext.Current.CancellationToken);

        // Assert
        var names = page.Hits.Take(top).Select(static hit => hit.Entry.Name).ToArray();
        foreach (var name in expected)
        {
            names.ShouldContain(name, $"«{query}»: {Describe(page)}");
        }
    }

    [Fact]
    public async Task Search_FolderFilter_OnlyInsideFolder()
    {
        // Act
        var page = await _service!.SearchAsync(new SearchRequest { Text = "in:Финансы", Limit = 50 }, TestContext.Current.CancellationToken);

        // Assert
        page.Hits.Count.ShouldBe(7);
        page.Hits.ShouldAllBe(static hit => hit.FullPath.Contains("Финансы", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Search_NoiseCorpus_CountsAreExact()
    {
        // Act
        var logs = await _service!.SearchAsync(new SearchRequest { Text = "app ext:log" }, TestContext.Current.CancellationToken);
        var images = await _service.SearchAsync(new SearchRequest { Text = "img type:image root:Photos" }, TestContext.Current.CancellationToken);

        // Assert
        logs.TotalCount.ShouldBe(120);
        images.TotalCount.ShouldBe(130);
    }

    private static string Describe(SearchPage page)
        => string.Join("; ", page.Hits.Take(6).Select(static hit => $"{hit.Entry.Name} [{hit.Score:F3}: {hit.RankReason}]"));

    private static async Task SeedAsync(SearchTestContext context)
    {
        await context.AddAsync(context.Projects,
            "DiskApp/README.md",
            new TestFile("DiskApp/PLAN.md", Modified: Now.AddDays(-1)),
            "DiskApp/docs/ARCHITECTURE.md",
            "DiskApp/docs/explanation.txt",
            new TestFile("DiskApp/src", IsDirectory: true),
            new TestFile("DiskApp/src/DiskApp.Core/Search/SearchService.cs", Modified: Now.AddHours(-2)),
            "DiskApp/src/DiskApp.Core/Search/SearchRanker.cs",
            "DiskApp/src/DiskApp.Core/Search/QueryParser.cs",
            "DiskApp/src/DiskApp.UI/Views/MainView.axaml",
            "DiskApp/src/DiskApp.UI/Views/SearchPageView.axaml",
            "Work/Финансы/Бюджет 2026.xlsx",
            "Work/Финансы/Бюджет 2025.xlsx",
            "Work/Финансы/Отчёт за квартал Q3.docx",
            "Work/Финансы/Отчёт за квартал Q2.docx",
            "Work/Финансы/Счёт-фактура 0412.pdf",
            new TestFile("Work/Финансы/invoice-2026-03.pdf", Modified: Now.AddDays(-40)),
            new TestFile("Work/Финансы/invoice-2026-04.pdf", Modified: Now.AddDays(-10)),
            "Work/Презентации/Стратегия 2027.pptx",
            "Work/Презентации/Квартальный обзор.pptx",
            new TestFile("Work/Q3 summary.txt", Modified: Now.AddHours(-1)),
            "Work/plan-2026.docx",
            "Personal/Документы/Паспорт скан.pdf",
            "Personal/Документы/Резюме.docx",
            "Personal/Документы/Договор аренды.pdf",
            "Downloads/setup-vscode-x64.exe",
            "Downloads/node-v20.msi",
            "Downloads/report (1).pdf",
            "Downloads/report (2).pdf",
            "Music/Lullaby.mp3",
            "Music/Morning.mp3",
            "Music/Night drive.flac");

        await context.AddAsync(context.Photos, "2026/Отпуск/море 001.jpg", "2026/Отпуск/море 002.jpg", "2026/Отпуск/горы 001.jpg");

        // Шум: журналы, кэш, снимки камеры — не должны обгонять осмысленные совпадения.
        await context.AddAsync(context.Projects, Enumerable.Range(0, 120).Select(static i => (TestFile)$"logs/app-2026-{1 + (i / 28):00}-{1 + (i % 28):00}.log").ToArray());
        await context.AddAsync(context.Projects, Enumerable.Range(0, 150).Select(static i => (TestFile)$"cache/blob{i:00000}.bin").ToArray());
        await context.AddAsync(context.Photos, Enumerable.Range(0, 130).Select(static i => (TestFile)$"2025/IMG_2025{1 + (i / 28):00}{1 + (i % 28):00}_{i:000000}.jpg").ToArray());

        await context.OpenAsync(context.Projects, "DiskApp/src/DiskApp.Core/Search/SearchService.cs", 30);
        await context.OpenAsync(context.Projects, "Work/Финансы/Бюджет 2026.xlsx", 10);
        await context.FavoriteAsync(context.Projects, "Downloads/report (2).pdf");
    }
}

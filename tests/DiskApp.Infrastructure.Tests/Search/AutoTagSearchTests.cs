using DiskApp.Core.Domain;
using DiskApp.Core.Paths;
using DiskApp.Core.Search;

using static DiskApp.Infrastructure.Tests.Search.SearchTestContext;

namespace DiskApp.Infrastructure.Tests.Search;

/// <summary>
/// <c>tag:</c> с авто-тегами папок (PLAN.md, этап 19): всё внутри папки на любой глубине, сама папка, но не соседняя папка
/// с тем же началом имени; отрицание; авто-тег корня; новый файл, вынесенный файл и пересборка индекса.
/// </summary>
public sealed class AutoTagSearchTests
{
    [Fact]
    public async Task Tag_AutoTagOfFolder_FindsFolderAndEverythingInside_NotSiblingWithSamePrefix()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.AddAsync(context.Projects, "417/план.docx", "417/Чертежи/лист.pdf", "417 ТЗ/чужой.docx", "4170/смета.xlsx", "Прочее/записка.docx");
        await context.AutoTagAsync(context.Projects, "417", "ТЗ");
        await context.TagAsync(context.Projects, "Прочее/записка.docx", "тз");

        // Act
        var found = await Search(context, "tag:тз");
        var excluded = await Search(context, "-tag:ТЗ ext:docx");

        // Assert: явный тег и авто-тег — один и тот же тег библиотеки
        Names(found).ShouldBe(["417", "план.docx", "Чертежи", "лист.pdf", "записка.docx"], ignoreOrder: true);
        found.TotalCount.ShouldBe(5);
        Names(excluded).ShouldBe(["чужой.docx"]);
    }

    [Fact]
    public async Task Tag_OwnTagInsideAutoTaggedFolderAndNestedFolders_EachEntryOnce()
    {
        // Arrange: свой тег у файла внутри папки, та же метка у вложенной папки и у самой себя — кандидаты не дублируются
        await using var context = await CreateAsync();
        await context.AddAsync(context.Projects, "417/план.docx", "417/Чертежи/лист.docx", "417/Чертежи/Старые/эскиз.docx");
        await context.AutoTagAsync(context.Projects, "417", "ТЗ");
        await context.AutoTagAsync(context.Projects, "417/Чертежи", "ТЗ");
        await context.TagAsync(context.Projects, "417/план.docx", "ТЗ");
        await context.TagAsync(context.Projects, "417/Чертежи", "ТЗ");

        // Act
        var all = await Search(context, "tag:тз");
        var documents = await Search(context, "tag:тз ext:docx");

        // Assert
        Names(all).ShouldBe(["417", "план.docx", "Чертежи", "лист.docx", "Старые", "эскиз.docx"], ignoreOrder: true);
        all.TotalCount.ShouldBe(6);
        Names(documents).ShouldBe(["план.docx", "лист.docx", "эскиз.docx"], ignoreOrder: true);
        documents.TotalCount.ShouldBe(3);
    }

    [Theory]
    [InlineData("tag:тз")]
    [InlineData("tag:тз ext:docx")]
    public async Task Tag_MoreMatchesThanCandidateLimit_TakesNewestFromTaggedFoldersOnly(string query)
    {
        // Arrange: совпадений больше лимита кандидатов — берутся самые новые, и только из папки с авто-тегом
        await using var context = await CreateAsync();
        await context.AddAsync(context.Projects, [.. Enumerable.Range(0, 30).Select(static i => new TestFile($"417/док-{i:00}.docx", Modified: Now.AddDays(-i)))]);
        await context.AddAsync(context.Projects, [.. Enumerable.Range(0, 30).Select(static i => new TestFile($"Прочее/новый-{i:00}.docx", Modified: Now.AddHours(-i)))]);
        await context.AutoTagAsync(context.Projects, "417", "ТЗ");
        var service = context.Service(new SearchOptions { CandidateLimit = 10 });

        // Act
        var page = await service.SearchAsync(new SearchRequest { Text = query, Limit = 50 }, TestContext.Current.CancellationToken);

        // Assert
        page.Hits.Count.ShouldBe(10);
        page.Hits.ShouldAllBe(hit => hit.Entry.Name.StartsWith("док-", StringComparison.Ordinal));
        Names(page).ShouldBe([.. Enumerable.Range(0, 10).Select(static i => $"док-{i:00}.docx")], ignoreOrder: true);
        page.TotalCount.ShouldBe(query.Contains("ext:", StringComparison.Ordinal) ? 30 : 31);
    }

    [Fact]
    public async Task Tag_FewTaggedEntriesAmongManyNewer_TakesNewestOfTagAndCountsExactly()
    {
        // Arrange: у тега чуть больше лимита записей, а вокруг сотни более новых — записи тега сортируются, а не ищутся
        // обходом индекса времени по всей базе (этап 19.5); число точное, хотя потолок подсчёта меньше
        await using var context = await CreateAsync();
        await context.AddAsync(context.Projects, [.. Enumerable.Range(0, 12).Select(static i => new TestFile($"417/док-{i:00}.docx", Modified: Now.AddDays(-i)))]);
        await context.AddAsync(context.Projects, [.. Enumerable.Range(0, 300).Select(static i => new TestFile($"Прочее/новый-{i:000}.docx", Modified: Now.AddMinutes(-i)))]);
        await context.AutoTagAsync(context.Projects, "417", "ТЗ");
        var service = context.Service(new SearchOptions { CandidateLimit = 10, CountCap = 5 });

        // Act
        var page = await service.SearchAsync(new SearchRequest { Text = "tag:тз", Limit = 50 }, TestContext.Current.CancellationToken);

        // Assert
        Names(page).ShouldBe([.. Enumerable.Range(0, 10).Select(static i => $"док-{i:00}.docx")], ignoreOrder: true);
        page.TotalCount.ShouldBe(13);
        page.IsTotalApproximate.ShouldBeFalse();
    }

    [Theory]
    [InlineData("tag:тз -ext:pdf", 21)]
    [InlineData("tag:тз is:file", 30)]
    [InlineData("tag:тз modified:2026", 30)]
    public async Task Tag_WithOtherFilterAndMoreMatchesThanLimit_TakesNewestMatches(string query, int total)
    {
        // Arrange: условие без индекса (сортировка записей тега) и с индексом (индекс выбирает SQLite)
        await using var context = await CreateAsync();
        await context.AddAsync(context.Projects, [.. Enumerable.Range(0, 30).Select(static i => new TestFile($"417/док-{i:00}.{(i % 3 == 2 ? "pdf" : "docx")}", Modified: Now.AddDays(-i)))]);
        await context.AddAsync(context.Projects, [.. Enumerable.Range(0, 30).Select(static i => new TestFile($"Прочее/новый-{i:00}.docx", Modified: Now.AddHours(-i)))]);
        await context.AutoTagAsync(context.Projects, "417", "ТЗ");
        var service = context.Service(new SearchOptions { CandidateLimit = 10 });

        // Act
        var page = await service.SearchAsync(new SearchRequest { Text = query, Limit = 50, Sort = SearchSortField.Modified, Descending = true }, TestContext.Current.CancellationToken);

        // Assert: самые новые совпадения, только из папки с авто-тегом
        var expected = Enumerable.Range(0, 30)
            .Where(i => !query.Contains("-ext:pdf", StringComparison.Ordinal) || i % 3 != 2)
            .Take(10)
            .Select(static i => $"док-{i:00}.{(i % 3 == 2 ? "pdf" : "docx")}");
        Names(page).ShouldBe([.. expected]);
        page.TotalCount.ShouldBe(total);
    }

    [Fact]
    public async Task Tag_RootInsideAutoTaggedFolder_FindsItsFiles()
    {
        // Arrange: «TCI2» — отдельный корень внутри папки «417» с авто-тегом; его файлы физически лежат в «417» (ADR-0015)
        await using var context = await CreateAsync();
        await context.AddAsync(context.Projects, "417/план.docx", "418/чужой.yml");
        var nested = await context.Roots.AddAsync(new Root { Path = Path.Combine(context.Projects.Path, "417", "TCI2"), Label = "TCI2" }, TestContext.Current.CancellationToken);
        await context.AddAsync(nested, "yml/a.yml", "yml/b.yml", "readme.md");
        await context.AutoTagAsync(context.Projects, "417", "ТЗ");
        await context.AutoTagAsync(context.Photos, string.Empty, "Фото");

        // Act
        var all = await Search(context, "tag:тз");
        var yaml = await Search(context, "tag:тз ext:yml");
        var newest = await context.Service(new SearchOptions { CandidateLimit = 2 }).SearchAsync(new SearchRequest { Text = "tag:тз ext:yml" }, TestContext.Current.CancellationToken);
        var outside = await Search(context, "-tag:тз ext:yml");

        // Assert
        Names(all).ShouldBe(["417", "план.docx", "yml", "a.yml", "b.yml", "readme.md"], ignoreOrder: true);
        all.TotalCount.ShouldBe(6);
        Names(yaml).ShouldBe(["a.yml", "b.yml"], ignoreOrder: true);
        Names(newest).ShouldBe(["a.yml", "b.yml"], ignoreOrder: true);
        Names(outside).ShouldBe(["чужой.yml"]);
    }

    [Fact]
    public async Task Tag_OwnTagsFarApartById_WithIndexedFilter_FindsBoth()
    {
        // Arrange: свои теги у записей с далеко разнесёнными идентификаторами — множество тега не битовая карта
        await using var context = await CreateAsync();
        await context.AddAsync(context.Projects, "a/первый.docx", "b/второй.docx", "c/третий.docx", "d/чужой.docx");
        var far = await context.GetAsync(context.Projects, "b/второй.docx");
        using (var connection = context.Database.Connections.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE entries SET id = 50000000 WHERE id = $id";
            command.Parameters.AddWithValue("$id", far.Id);
            command.ExecuteNonQuery();
        }

        await context.TagAsync(context.Projects, "a/первый.docx", "Важное");
        await context.TagAsync(context.Projects, "b/второй.docx", "Важное");
        await context.TagAsync(context.Projects, "c/третий.docx", "Важное");

        // Act
        var all = await Search(context, "tag:важное ext:docx");

        // Assert
        Names(all).ShouldBe(["первый.docx", "второй.docx", "третий.docx"], ignoreOrder: true);
        all.TotalCount.ShouldBe(3);
    }

    [Fact]
    public async Task Tag_WithTypo_FuzzyCandidatesKeepTagCondition()
    {
        // Arrange: нечёткий поиск берёт кандидатов из FTS, условие тега остаётся проверкой по пути
        await using var context = await CreateAsync();
        await context.AddAsync(context.Projects, "417/report.docx", "Прочее/report.docx");
        await context.AutoTagAsync(context.Projects, "417", "ТЗ");

        // Act
        var found = await Search(context, "tag:тз reprot");

        // Assert
        found.Hits.Select(static hit => hit.Entry.RelPath).ShouldBe([Path.Combine("417", "report.docx")]);
        found.IncludesFuzzy.ShouldBeTrue();
    }

    [Fact]
    public async Task Tag_AutoTagOfRoot_FindsWholeRootOnly()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.AddAsync(context.Projects, "docs/a.jpg");
        await context.AddAsync(context.Photos, "2026/море.jpg", "2025/горы.jpg");
        await context.AutoTagAsync(context.Photos, string.Empty, "Фото");

        // Act
        var found = await Search(context, "tag:фото ext:jpg");

        // Assert
        Names(found).ShouldBe(["море.jpg", "горы.jpg"], ignoreOrder: true);
    }

    [Fact]
    public async Task Tag_NestedAutoTags_CombineWithAnd()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.AddAsync(context.Projects, "417/Чертежи/лист.pdf", "417/смета.xlsx", "418/Чертежи/план.pdf");
        await context.AutoTagAsync(context.Projects, "417", "Проект 417");
        await context.AutoTagAsync(context.Projects, "417/Чертежи", "Чертежи");
        await context.AutoTagAsync(context.Projects, "418/Чертежи", "Чертежи");

        // Act
        var both = await Search(context, "tag:\"проект 417\" tag:чертежи ext:pdf");
        var drawingsOnly = await Search(context, "tag:чертежи -tag:\"проект 417\" ext:pdf");

        // Assert
        Names(both).ShouldBe(["лист.pdf"]);
        Names(drawingsOnly).ShouldBe(["план.pdf"]);
    }

    [Fact]
    public async Task Tag_FileMovedOut_LosesAutoTag_NewFileGetsIt()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.AddAsync(context.Projects, "417/старый.txt", "Архив/readme.md");
        await context.AutoTagAsync(context.Projects, "417", "ТЗ");
        var separator = PathNormalizer.Current.Separator;

        // Act: перенос и новый файл — так их записывает наблюдатель
        await context.Entries.RenameSubtreeAsync(context.Projects.Id, $"417{separator}старый.txt", $"Архив{separator}старый.txt", TestContext.Current.CancellationToken);
        await context.AddAsync(context.Projects, "417/новый.txt");
        var found = await Search(context, "tag:тз ext:txt");

        // Assert
        Names(found).ShouldBe(["новый.txt"]);
    }

    [Fact]
    public async Task Tag_AfterIndexRebuild_AutoTagsStay()
    {
        // Arrange: пересборка удаляет записи вместе с явными тегами
        await using var context = await CreateAsync();
        await context.AddAsync(context.Projects, "417/план.docx", "Прочее/записка.docx");
        await context.AutoTagAsync(context.Projects, "417", "ТЗ");
        await context.TagAsync(context.Projects, "Прочее/записка.docx", "ТЗ");

        // Act
        await context.Entries.DeleteByRootAsync(context.Projects.Id, TestContext.Current.CancellationToken);
        await context.AddAsync(context.Projects, "417/план.docx", "Прочее/записка.docx");
        var found = await Search(context, "tag:тз ext:docx");

        // Assert
        Names(found).ShouldBe(["план.docx"]);
    }

    private static Task<SearchPage> Search(SearchTestContext context, string text)
        => context.Service().SearchAsync(new SearchRequest { Text = text }, TestContext.Current.CancellationToken);

    private static List<string> Names(SearchPage page) => page.Hits.Select(static hit => hit.Entry.Name).ToList();
}

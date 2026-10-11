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

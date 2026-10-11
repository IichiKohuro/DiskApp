using System.Net;
using System.Net.Http.Json;

using DiskApp.Contracts;
using DiskApp.Contracts.Library;
using DiskApp.Contracts.Search;

namespace DiskApp.Server.Tests.Api;

/// <summary>
/// Авто-теги папок через API (PLAN.md, этап 19): тег папки находит всё внутри неё по <c>tag:</c>, записи получают его как
/// унаследованный, он переживает пересборку индекса и переименование папки, а файл, вынесенный из папки, его теряет.
/// </summary>
public sealed class AutoTagsApiTests
{
    [Fact]
    public async Task SetAutoTags_FolderTagFindsEverythingInsideButNotSiblingWithSamePrefix()
    {
        // Arrange: «417 ТЗ» начинается с «417», но лежит рядом, а не внутри
        await using var context = await ServerTestContext.StartAsync();
        var folder = context.Files.CreateDirectory(Path.Combine("Работа", "417"));
        context.Files.CreateFile(Path.Combine("Работа", "417", "план.docx"), "1");
        context.Files.CreateFile(Path.Combine("Работа", "417", "Чертежи", "лист.pdf"), "2");
        context.Files.CreateFile(Path.Combine("Работа", "417 ТЗ", "чужой.docx"), "3");
        await context.AddRootAsync();

        // Act
        var saved = await PutAsync(context, folder, "ТЗ", "417", "тз");
        var listed = await context.GetAsync(ApiRoutes.V1.AutoTags, DiskAppJsonContext.Default.IReadOnlyListFolderTagsDto);
        var found = await SearchAsync(context, "tag:тз");
        var excluded = await SearchAsync(context, "-tag:ТЗ ext:docx");

        // Assert
        saved.Path.ShouldBe(folder);
        saved.Tags.Select(static tag => tag.Name).ShouldBe(["417", "ТЗ"], ignoreOrder: true);
        listed.ShouldHaveSingleItem().Path.ShouldBe(folder);
        found.Items.Select(static item => item.Name).ShouldBe(["417", "план.docx", "Чертежи", "лист.pdf"], ignoreOrder: true);
        excluded.Items.Select(static item => item.Name).ShouldBe(["чужой.docx"]);
        var plan = found.Items.Single(static item => item.Name == "план.docx");
        plan.Tags.ShouldBeNull();
        plan.InheritedTags.ShouldNotBeNull().Select(static tag => tag.Name).ShouldBe(["417", "ТЗ"], ignoreOrder: true);
        plan.InheritedTags!.ShouldAllBe(tag => tag.Folder == folder);
    }

    [Fact]
    public async Task NestedAutoTags_AddUpAndNearestFolderComesFirst()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var project = context.Files.CreateDirectory("417");
        var drawings = context.Files.CreateDirectory(Path.Combine("417", "Чертежи"));
        context.Files.CreateFile(Path.Combine("417", "Чертежи", "лист.pdf"), "1");
        context.Files.CreateFile(Path.Combine("417", "смета.xlsx"), "2");
        await context.AddRootAsync();
        await PutAsync(context, project, "Проект 417");
        await PutAsync(context, drawings, "Чертежи", "Проект 417");

        // Act
        var both = await SearchAsync(context, "tag:\"проект 417\" tag:чертежи ext:pdf");
        var sheet = both.Items.ShouldHaveSingleItem();

        // Assert: «Проект 417» есть у ближайшей папки — от дальней второй раз не показывается
        sheet.Name.ShouldBe("лист.pdf");
        sheet.InheritedTags!.Select(static tag => (tag.Name, tag.Folder)).ShouldBe([("Проект 417", drawings), ("Чертежи", drawings)], ignoreOrder: true);
        (await SearchAsync(context, "tag:\"проект 417\" ext:xlsx")).Items.ShouldHaveSingleItem().Name.ShouldBe("смета.xlsx");
    }

    [Fact]
    public async Task NewFileInside_IsFoundByAutoTag_FileMovedOut_IsNot()
    {
        // Arrange: наблюдатель видит изменения мимо DiskApp, как из Проводника
        await using var context = await ServerTestContext.StartAsync();
        var folder = context.Files.CreateDirectory("417");
        context.Files.CreateDirectory("Архив");
        context.Files.CreateFile(Path.Combine("417", "старый.txt"), "1");
        await context.AddRootAsync();
        await PutAsync(context, folder, "ТЗ");

        // Act
        context.Files.CreateFile(Path.Combine("417", "новый.txt"), "2");
        File.Move(Path.Combine(folder, "старый.txt"), context.Files.Resolve(Path.Combine("Архив", "старый.txt")));
        var found = await WaitForAsync(context, "tag:тз ext:txt", static page => page.Items.Select(static item => item.Name).SequenceEqual(["новый.txt"]));

        // Assert
        found.Items.Select(static item => item.Name).ShouldBe(["новый.txt"]);
        (await SearchAsync(context, "старый")).Items.ShouldHaveSingleItem().InheritedTags.ShouldBeNull();
    }

    [Fact]
    public async Task RenameFolderOnDisk_AutoTagsFollow()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var folder = context.Files.CreateDirectory("417");
        context.Files.CreateFile(Path.Combine("417", "план.docx"), "1");
        await context.AddRootAsync();
        await PutAsync(context, folder, "ТЗ");
        var renamed = context.Files.Resolve("417 Мост");

        // Act
        Directory.Move(folder, renamed);
        var folders = await WaitForFoldersAsync(context, renamed);
        var found = await WaitForAsync(context, "tag:тз ext:docx", static page => page.Items.Count == 1);

        // Assert
        folders.ShouldHaveSingleItem().Path.ShouldBe(renamed);
        found.Items.ShouldHaveSingleItem().FullPath.ShouldBe(Path.Combine(renamed, "план.docx"));
    }

    [Fact]
    public async Task RebuildIndex_KeepsAutoTags()
    {
        // Arrange: пересборка удаляет записи — явные теги теряются, авто-теги папки остаются
        await using var context = await ServerTestContext.StartAsync();
        var folder = context.Files.CreateDirectory("417");
        context.Files.CreateFile(Path.Combine("417", "план.docx"), "1");
        await context.AddRootAsync();
        await PutAsync(context, folder, "ТЗ");

        // Act
        using var rebuild = await context.Client.PostAsync(new Uri(ApiRoutes.V1.IndexRebuild, UriKind.Relative), null, TestContext.Current.CancellationToken);
        await context.WaitIdleAsync();
        var found = await SearchAsync(context, "tag:тз ext:docx");

        // Assert
        rebuild.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        found.Items.ShouldHaveSingleItem().Name.ShouldBe("план.docx");
    }

    [Fact]
    public async Task EmptyList_RemovesAutoTags_EvenForMissingFolder()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var folder = context.Files.CreateDirectory("417");
        await context.AddRootAsync();
        await PutAsync(context, folder, "ТЗ");
        Directory.Delete(folder);

        // Act
        var cleared = await PutAsync(context, folder);
        var listed = await context.GetAsync(ApiRoutes.V1.AutoTags, DiskAppJsonContext.Default.IReadOnlyListFolderTagsDto);
        var tags = await context.GetAsync(ApiRoutes.V1.Tags, DiskAppJsonContext.Default.IReadOnlyListTagDto);

        // Assert: сам тег остаётся в библиотеке
        cleared.Tags.ShouldBeEmpty();
        listed.ShouldBeEmpty();
        tags.ShouldContain(tag => tag.Name == "ТЗ");
    }

    [Fact]
    public async Task SetAutoTags_InvalidRequests_AreRejected()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var folder = context.Files.CreateDirectory("417");
        await context.AddRootAsync();
        using var outside = new TemporaryDirectory("outside");

        // Act
        using var relative = await SendAsync(context, "417", "ТЗ");
        using var foreign = await SendAsync(context, outside.FullPath, "ТЗ");
        using var missing = await SendAsync(context, Path.Combine(folder, "нет"), "ТЗ");
        using var tooMany = await SendAsync(context, folder, [.. Enumerable.Range(1, SetFolderTagsRequest.MaxTags + 1).Select(static index => "t" + index)]);
        using var tooLong = await SendAsync(context, folder, new string('я', 65));

        // Assert
        relative.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        foreign.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        tooMany.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        tooLong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeleteTag_RemovesItFromFolders()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var folder = context.Files.CreateDirectory("417");
        await context.AddRootAsync();
        var saved = await PutAsync(context, folder, "ТЗ", "Мост");

        // Act
        using var delete = await context.Client.DeleteAsync(new Uri(ApiRoutes.V1.Tag(saved.Tags.Single(static tag => tag.Name == "ТЗ").Id), UriKind.Relative), TestContext.Current.CancellationToken);
        var listed = await context.GetAsync(ApiRoutes.V1.AutoTags, DiskAppJsonContext.Default.IReadOnlyListFolderTagsDto);

        // Assert
        delete.EnsureSuccessStatusCode();
        listed.ShouldHaveSingleItem().Tags.Select(static tag => tag.Name).ShouldBe(["Мост"]);
    }

    private static async Task<FolderTagsDto> PutAsync(ServerTestContext context, string path, params string[] tags)
    {
        using var response = await SendAsync(context, path, tags);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(DiskAppJsonContext.Default.FolderTagsDto, TestContext.Current.CancellationToken))!;
    }

    private static Task<HttpResponseMessage> SendAsync(ServerTestContext context, string path, params string[] tags)
        => context.Client.PutAsJsonAsync(ApiRoutes.V1.AutoTags, new SetFolderTagsRequest { Path = path, Tags = tags }, DiskAppJsonContext.Default.SetFolderTagsRequest, TestContext.Current.CancellationToken);

    private static Task<SearchResponse> SearchAsync(ServerTestContext context, string query)
        => context.GetAsync(ApiRoutes.V1.Search + "?limit=50&q=" + Uri.EscapeDataString(query), DiskAppJsonContext.Default.SearchResponse);

    private static async Task<SearchResponse> WaitForAsync(ServerTestContext context, string query, Func<SearchResponse, bool> done)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        var page = await SearchAsync(context, query);
        while (!done(page) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            page = await SearchAsync(context, query);
        }

        return page;
    }

    private static async Task<IReadOnlyList<FolderTagsDto>> WaitForFoldersAsync(ServerTestContext context, string expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        var folders = await context.GetAsync(ApiRoutes.V1.AutoTags, DiskAppJsonContext.Default.IReadOnlyListFolderTagsDto);
        while (!folders.Any(folder => string.Equals(folder.Path, expected, StringComparison.OrdinalIgnoreCase)) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            folders = await context.GetAsync(ApiRoutes.V1.AutoTags, DiskAppJsonContext.Default.IReadOnlyListFolderTagsDto);
        }

        return folders;
    }
}

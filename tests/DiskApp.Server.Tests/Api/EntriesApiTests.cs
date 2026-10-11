using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using DiskApp.Contracts;
using DiskApp.Contracts.Library;
using DiskApp.Core.Results;

namespace DiskApp.Server.Tests.Api;

/// <summary>
/// Записи через API: карточка, содержимое папки, обзор по пути только внутри корней, содержимое файла с Range,
/// иконки с ETag, действия оболочки со статистикой открытий, теги записи.
/// </summary>
public sealed class EntriesApiTests
{
    [Fact]
    public async Task BrowseAndChildren_ListFoldersFirstAndStayInsideRoots()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        context.Files.CreateFile(Path.Combine("projects", "zeta.txt"), "z");
        context.Files.CreateFile(Path.Combine("projects", "alpha.txt"), "a");
        context.Files.CreateFile(Path.Combine("projects", "src", "main.cs"), "m");
        var root = await context.AddRootAsync();

        // Act
        var top = await context.GetAsync(ApiRoutes.V1.Browse + "?path=" + Uri.EscapeDataString(context.Files.FullPath), DiskAppJsonContext.Default.BrowseResponse);
        var projects = await context.GetAsync(ApiRoutes.V1.Browse + "?path=" + Uri.EscapeDataString(context.Files.Resolve("projects")), DiskAppJsonContext.Default.BrowseResponse);
        var byId = await context.GetAsync(ApiRoutes.V1.Entry(projects.Folder!.Id) + "/children?limit=2", DiskAppJsonContext.Default.BrowseResponse);
        var outside = await context.Client.GetAsync(new Uri(ApiRoutes.V1.Browse + "?path=" + Uri.EscapeDataString(context.Data.FullPath), UriKind.Relative), TestContext.Current.CancellationToken);
        var escape = await context.Client.GetAsync(new Uri(ApiRoutes.V1.Browse + "?path=" + Uri.EscapeDataString(Path.Combine(context.Files.FullPath, "projects", "..", "..")), UriKind.Relative), TestContext.Current.CancellationToken);
        var file = projects.Items.Single(static item => item.Name == "alpha.txt");
        var browseFile = await context.Client.GetAsync(new Uri(ApiRoutes.V1.Browse + "?path=" + Uri.EscapeDataString(file.FullPath), UriKind.Relative), TestContext.Current.CancellationToken);

        // Assert
        top.RootId.ShouldBe(root.Id);
        top.Folder.ShouldBeNull();
        top.Items.ShouldHaveSingleItem().Name.ShouldBe("projects");
        projects.Items.Select(static item => item.Name).ShouldBe(["src", "alpha.txt", "zeta.txt"]);
        projects.Items[0].IconKey.ShouldBe("folder");
        byId.Items.Count.ShouldBe(2);
        byId.Truncated.ShouldBeTrue();
        outside.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        escape.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        browseFile.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Content_FullAndRange_StreamsFileBytes()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        context.Files.CreateFile("data.txt", "0123456789");
        await context.AddRootAsync();
        var entry = (await context.GetAsync(ApiRoutes.V1.Search + "?q=data", DiskAppJsonContext.Default.SearchResponse)).Items.ShouldHaveSingleItem();
        var uri = new Uri(ApiRoutes.V1.Entry(entry.Id) + "/content", UriKind.Relative);

        // Act
        var full = await context.Client.GetStringAsync(uri, TestContext.Current.CancellationToken);
        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, uri);
        rangeRequest.Headers.Range = new RangeHeaderValue(2, 4);
        using var range = await context.Client.SendAsync(rangeRequest, TestContext.Current.CancellationToken);
        var partial = await range.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert
        full.ShouldBe("0123456789");
        range.StatusCode.ShouldBe(HttpStatusCode.PartialContent);
        partial.ShouldBe("234");
        range.Content.Headers.ContentType!.MediaType.ShouldBe("text/plain");
    }

    [Fact]
    public async Task CloudPlaceholder_IndexedButContentAndThumbnailNotRead()
    {
        // Arrange: признак «вне сети» — тот же, что у файлов OneDrive «только в облаке»; чтение скачало бы файл
        await using var context = await ServerTestContext.StartAsync();
        var path = context.Files.CreateFile("cloud video.mp4", "not downloaded");
        File.SetAttributes(path, FileAttributes.Offline);
        await context.AddRootAsync();

        // Act
        var entry = (await context.GetAsync(ApiRoutes.V1.Search + "?q=cloud", DiskAppJsonContext.Default.SearchResponse)).Items.ShouldHaveSingleItem();
        using var content = await context.Client.GetAsync(new Uri(ApiRoutes.V1.Entry(entry.Id) + "/content", UriKind.Relative), TestContext.Current.CancellationToken);
        using var thumbnail = await context.Client.GetAsync(new Uri(ApiRoutes.V1.Entry(entry.Id) + "/thumbnail", UriKind.Relative), TestContext.Current.CancellationToken);

        // Assert
        entry.IsCloudPlaceholder.ShouldBeTrue();
        content.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await content.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("entry.cloud_placeholder");
        thumbnail.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Open_RecordsUsageAndCallsShell()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var path = context.Files.CreateFile("contract.docx", "c");
        await context.AddRootAsync();
        var entry = (await context.GetAsync(ApiRoutes.V1.Search + "?q=contract", DiskAppJsonContext.Default.SearchResponse)).Items.ShouldHaveSingleItem();

        // Act
        var open = await context.Client.PostAsync(new Uri(ApiRoutes.V1.Entry(entry.Id) + "/open", UriKind.Relative), null, TestContext.Current.CancellationToken);
        var reveal = await context.Client.PostAsync(new Uri(ApiRoutes.V1.Entry(entry.Id) + "/reveal", UriKind.Relative), null, TestContext.Current.CancellationToken);
        var after = await context.GetAsync(ApiRoutes.V1.Entry(entry.Id), DiskAppJsonContext.Default.EntryDto);
        var recent = await context.GetAsync(ApiRoutes.V1.Recent, DiskAppJsonContext.Default.IReadOnlyListEntryDto);

        // Assert
        open.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        reveal.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        context.Shell!.Calls.ShouldBe([("open", path), ("reveal", path)]);
        after.OpenCount.ShouldBe(1, "показ в Проводнике не считается открытием");
        after.LastOpenedUtc.ShouldNotBeNull();
        recent.ShouldHaveSingleItem().Id.ShouldBe(entry.Id);
    }

    [Fact]
    public async Task Open_ShellFails_Returns422WithCode()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        context.Files.CreateFile("broken.lnk", "l");
        await context.AddRootAsync();
        var entry = (await context.GetAsync(ApiRoutes.V1.Search + "?q=broken", DiskAppJsonContext.Default.SearchResponse)).Items.ShouldHaveSingleItem();
        context.Shell!.FailWith = new DomainError("shell.no_association", "Нет приложения для этого типа файлов.");

        // Act
        var response = await context.Client.PostAsync(new Uri(ApiRoutes.V1.Entry(entry.Id) + "/open", UriKind.Relative), null, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var after = await context.GetAsync(ApiRoutes.V1.Entry(entry.Id), DiskAppJsonContext.Default.EntryDto);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        body.ShouldContain("shell.no_association");
        after.OpenCount.ShouldBe(0);
        after.IconKey.ShouldBe("entry:" + entry.Id, "у ярлыка своя иконка");
    }

    [Fact]
    public async Task Actions_WithoutPlatformShell_Return501()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync(withPlatform: false);
        context.Files.CreateFile("photo.jpg", "p");
        await context.AddRootAsync();
        var entry = (await context.GetAsync(ApiRoutes.V1.Search + "?q=photo", DiskAppJsonContext.Default.SearchResponse)).Items.ShouldHaveSingleItem();

        // Act
        var open = await context.Client.PostAsync(new Uri(ApiRoutes.V1.Entry(entry.Id) + "/open", UriKind.Relative), null, TestContext.Current.CancellationToken);
        var icon = await context.Client.GetAsync(new Uri(ApiRoutes.V1.Entry(entry.Id) + "/icon", UriKind.Relative), TestContext.Current.CancellationToken);

        // Assert
        open.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        icon.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
    }

    [Fact]
    public async Task Icon_CyrillicExtension_ReturnsPngWithAsciiETag()
    {
        // Arrange: расширение кириллицей попадало в ETag как есть, и Kestrel отвечал 500 (журнал 2026-10-11)
        await using var context = await ServerTestContext.StartAsync();
        context.Files.CreateFile("база.бд", "b");
        await context.AddRootAsync();
        var entry = (await context.GetAsync(ApiRoutes.V1.Search + "?q=" + Uri.EscapeDataString("база"), DiskAppJsonContext.Default.SearchResponse)).Items.ShouldHaveSingleItem();
        var uri = new Uri(ApiRoutes.V1.Entry(entry.Id) + "/icon?size=48", UriKind.Relative);

        // Act
        using var first = await context.Client.GetAsync(uri, TestContext.Current.CancellationToken);
        using var conditional = new HttpRequestMessage(HttpMethod.Get, uri);
        conditional.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var second = await context.Client.SendAsync(conditional, TestContext.Current.CancellationToken);

        // Assert
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        first.Headers.ETag!.Tag.ShouldBe("\"ext:%D0%B1%D0%B4:48\"");
        second.StatusCode.ShouldBe(HttpStatusCode.NotModified);
    }

    [Fact]
    public async Task Icon_ReturnsPngWithETagAndHonorsIfNoneMatch()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        context.Files.CreateFile("slides.pptx", "s");
        await context.AddRootAsync();
        var entry = (await context.GetAsync(ApiRoutes.V1.Search + "?q=slides", DiskAppJsonContext.Default.SearchResponse)).Items.ShouldHaveSingleItem();
        var uri = new Uri(ApiRoutes.V1.Entry(entry.Id) + "/icon?size=48", UriKind.Relative);

        // Act
        using var first = await context.Client.GetAsync(uri, TestContext.Current.CancellationToken);
        var bytes = await first.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        using var conditional = new HttpRequestMessage(HttpMethod.Get, uri);
        conditional.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        using var second = await context.Client.SendAsync(conditional, TestContext.Current.CancellationToken);
        var badSize = await context.Client.GetAsync(new Uri(ApiRoutes.V1.Entry(entry.Id) + "/icon?size=17", UriKind.Relative), TestContext.Current.CancellationToken);
        var thumbnail = await context.Client.GetAsync(new Uri(ApiRoutes.V1.Entry(entry.Id) + "/thumbnail", UriKind.Relative), TestContext.Current.CancellationToken);

        // Assert
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        first.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        first.Headers.ETag!.Tag.ShouldBe("\"ext:pptx:48\"");
        bytes.ShouldBe(FakeIcons.Png);
        second.StatusCode.ShouldBe(HttpStatusCode.NotModified);
        badSize.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        thumbnail.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SetTags_ReplacesEntryTags()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        context.Files.CreateFile("plan.md", "p");
        await context.AddRootAsync();
        var entry = (await context.GetAsync(ApiRoutes.V1.Search + "?q=plan", DiskAppJsonContext.Default.SearchResponse)).Items.ShouldHaveSingleItem();
        var work = await CreateTagAsync(context, "работа");
        var urgent = await CreateTagAsync(context, "срочно");

        // Act
        var first = await context.Client.PostAsJsonAsync(ApiRoutes.V1.Entry(entry.Id) + "/tags", new EntryTagsRequest { TagIds = [work.Id, urgent.Id] },
            DiskAppJsonContext.Default.EntryTagsRequest, TestContext.Current.CancellationToken);
        var second = await context.Client.PostAsJsonAsync(ApiRoutes.V1.Entry(entry.Id) + "/tags", new EntryTagsRequest { TagIds = [urgent.Id] },
            DiskAppJsonContext.Default.EntryTagsRequest, TestContext.Current.CancellationToken);
        var updated = await second.Content.ReadFromJsonAsync(DiskAppJsonContext.Default.EntryDto, TestContext.Current.CancellationToken);
        var search = await context.GetAsync(ApiRoutes.V1.Search + "?q=plan", DiskAppJsonContext.Default.SearchResponse);
        var unknown = await context.Client.PostAsJsonAsync(ApiRoutes.V1.Entry(entry.Id) + "/tags", new EntryTagsRequest { TagIds = [999] },
            DiskAppJsonContext.Default.EntryTagsRequest, TestContext.Current.CancellationToken);

        // Assert
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        updated!.Tags.ShouldNotBeNull().ShouldHaveSingleItem().Name.ShouldBe("срочно");
        search.Items.ShouldHaveSingleItem().Tags.ShouldNotBeNull().ShouldHaveSingleItem().Id.ShouldBe(urgent.Id);
        unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UnknownEntry_Returns404()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();

        // Act
        var entry = await context.Client.GetAsync(new Uri(ApiRoutes.V1.Entry(12345), UriKind.Relative), TestContext.Current.CancellationToken);
        var open = await context.Client.PostAsync(new Uri(ApiRoutes.V1.Entry(12345) + "/open", UriKind.Relative), null, TestContext.Current.CancellationToken);

        // Assert
        entry.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        open.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    internal static async Task<TagDto> CreateTagAsync(ServerTestContext context, string name, string? color = null)
    {
        var response = await context.Client.PostAsJsonAsync(ApiRoutes.V1.Tags, new TagRequest { Name = name, Color = color }, DiskAppJsonContext.Default.TagRequest, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(DiskAppJsonContext.Default.TagDto, TestContext.Current.CancellationToken))!;
    }
}

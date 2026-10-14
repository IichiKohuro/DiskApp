using System.Net;
using System.Net.Http.Json;

using DiskApp.Contracts;
using DiskApp.Contracts.Roots;

namespace DiskApp.Server.Tests.Api;

/// <summary>
/// Корни и индекс через API: добавление с проверкой пути, изменение, удаление без касания файлов, пауза,
/// пересканирование, состояние и пересборка.
/// </summary>
public sealed class RootsApiTests
{
    [Fact]
    public async Task Post_ExistingFolder_CreatesRootAndIndexesFiles()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        context.Files.CreateFile(Path.Combine("docs", "Quarterly Report.pdf"), "pdf");

        // Act
        var response = await context.Client.PostAsJsonAsync(ApiRoutes.V1.Roots, new CreateRootRequest { Path = context.Files.FullPath, Label = "Файлы" },
            DiskAppJsonContext.Default.CreateRootRequest, TestContext.Current.CancellationToken);
        var created = await response.Content.ReadFromJsonAsync(DiskAppJsonContext.Default.RootDto, TestContext.Current.CancellationToken);
        await context.WaitIdleAsync();
        var roots = await context.GetAsync(ApiRoutes.V1.Roots, DiskAppJsonContext.Default.IReadOnlyListRootDto);
        var status = await context.GetAsync(ApiRoutes.V1.IndexStatus, DiskAppJsonContext.Default.IndexStatusDto);
        var search = await context.GetAsync(ApiRoutes.V1.Search + "?q=quarterly", DiskAppJsonContext.Default.SearchResponse);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location!.ToString().ShouldBe(ApiRoutes.V1.Root(created!.Id));
        created.Label.ShouldBe("Файлы");
        roots.ShouldHaveSingleItem().Status.ShouldBe(RootStatus.Online);
        status.State.ShouldBe(IndexerState.Idle);
        status.Roots.ShouldHaveSingleItem().LastScanUtc.ShouldNotBeNull();
        search.Items.ShouldHaveSingleItem().Name.ShouldBe("Quarterly Report.pdf");
    }

    [Theory]
    [InlineData("relative\\path")]
    [InlineData("")]
    public async Task Post_InvalidPath_Returns400(string path)
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();

        // Act
        var response = await context.Client.PostAsJsonAsync(ApiRoutes.V1.Roots, new CreateRootRequest { Path = path }, DiskAppJsonContext.Default.CreateRootRequest, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task GetStats_IndexedRoot_ReturnsCountsAndSize()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        context.Files.CreateFile("a.txt", "12345");
        context.Files.CreateFile(Path.Combine("sub", "b.txt"), "123");
        var root = await context.AddRootAsync();

        // Act
        var stats = await context.GetAsync(ApiRoutes.V1.Root(root.Id) + "/stats", DiskAppJsonContext.Default.RootStatsDto);
        var missing = await context.Client.GetAsync(ApiRoutes.V1.Root(999) + "/stats", TestContext.Current.CancellationToken);

        // Assert
        stats.RootId.ShouldBe(root.Id);
        stats.FileCount.ShouldBe(2);
        stats.DirectoryCount.ShouldBe(1);
        stats.TotalSize.ShouldBe(8);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Post_MissingFolderOrDuplicate_Returns400Or409()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        await context.AddRootAsync();

        // Act
        var missing = await context.Client.PostAsJsonAsync(ApiRoutes.V1.Roots, new CreateRootRequest { Path = context.Files.Resolve("nope") },
            DiskAppJsonContext.Default.CreateRootRequest, TestContext.Current.CancellationToken);
        var duplicate = await context.Client.PostAsJsonAsync(ApiRoutes.V1.Roots, new CreateRootRequest { Path = context.Files.FullPath.ToUpperInvariant() },
            DiskAppJsonContext.Default.CreateRootRequest, TestContext.Current.CancellationToken);
        var body = await duplicate.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        body.ShouldContain("root.duplicate");
    }

    [Fact]
    public async Task Put_ChangesLabelAndDisables()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var root = await context.AddRootAsync();

        // Act
        var response = await context.Client.PutAsJsonAsync(ApiRoutes.V1.Root(root.Id), new UpdateRootRequest { Label = "Работа", IsEnabled = false },
            DiskAppJsonContext.Default.UpdateRootRequest, TestContext.Current.CancellationToken);
        var updated = await response.Content.ReadFromJsonAsync(DiskAppJsonContext.Default.RootDto, TestContext.Current.CancellationToken);
        var emptyLabel = await context.Client.PutAsJsonAsync(ApiRoutes.V1.Root(root.Id), new UpdateRootRequest { Label = " " },
            DiskAppJsonContext.Default.UpdateRootRequest, TestContext.Current.CancellationToken);
        var unknown = await context.Client.PutAsJsonAsync(ApiRoutes.V1.Root(999), new UpdateRootRequest { Label = "x" },
            DiskAppJsonContext.Default.UpdateRootRequest, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        updated!.Label.ShouldBe("Работа");
        updated.IsEnabled.ShouldBeFalse();
        context.Indexer.GetStatuses().ShouldBeEmpty("выключенный корень отключён от индексатора");
        emptyLabel.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_RemovesRootAndIndexButKeepsFiles()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var file = context.Files.CreateFile("keep-me.txt", "data");
        var root = await context.AddRootAsync();
        var entry = (await context.GetAsync(ApiRoutes.V1.Search + "?q=keep-me", DiskAppJsonContext.Default.SearchResponse)).Items.ShouldHaveSingleItem();

        // Act
        var response = await context.Client.DeleteAsync(new Uri(ApiRoutes.V1.Root(root.Id), UriKind.Relative), TestContext.Current.CancellationToken);
        var entryAfter = await context.Client.GetAsync(new Uri(ApiRoutes.V1.Entry(entry.Id), UriKind.Relative), TestContext.Current.CancellationToken);
        var again = await context.Client.DeleteAsync(new Uri(ApiRoutes.V1.Root(root.Id), UriKind.Relative), TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        entryAfter.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        again.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        File.Exists(file).ShouldBeTrue("файлы на диске не трогаются");
        (await context.GetAsync(ApiRoutes.V1.Roots, DiskAppJsonContext.Default.IReadOnlyListRootDto)).ShouldBeEmpty();
    }

    [Fact]
    public async Task NestedRoots_FileBelongsToDeepestRootAndKeepsIdWhenRootsChange()
    {
        // Arrange: TCI2 добавлен корнем, затем корнем добавляют и 10S-417, который его содержит (ADR-0015).
        await using var context = await ServerTestContext.StartAsync();
        context.Files.CreateFile(Path.Combine("10S-417", "TCI2", "yml", "dnp3_sett.yml"), "yml");
        context.Files.CreateFile(Path.Combine("10S-417", "readme.txt"), "txt");
        var inner = await context.AddRootAsync(Path.Combine("10S-417", "TCI2"));
        var innerHit = (await context.GetAsync(ApiRoutes.V1.Search + "?q=dnp3", DiskAppJsonContext.Default.SearchResponse)).Items.ShouldHaveSingleItem();

        // Act: внешний корень поверх внутреннего.
        var outer = await context.AddRootAsync("10S-417");
        var afterOuter = await context.GetAsync(ApiRoutes.V1.Search + "?q=dnp3", DiskAppJsonContext.Default.SearchResponse);
        var inOuter = await context.GetAsync(ApiRoutes.V1.Search + "?q=dnp3%20in:10S-417", DiskAppJsonContext.Default.SearchResponse);
        var readme = await context.GetAsync(ApiRoutes.V1.Search + "?q=readme", DiskAppJsonContext.Default.SearchResponse);

        // Внутренний корень убрали: файл переходит внешнему с тем же идентификатором.
        (await context.Client.DeleteAsync(new Uri(ApiRoutes.V1.Root(inner.Id), UriKind.Relative), TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        await context.WaitIdleAsync();
        var afterDelete = await context.GetAsync(ApiRoutes.V1.Search + "?q=dnp3", DiskAppJsonContext.Default.SearchResponse);

        // Внутренний корень добавили снова: файл переходит ему, идентификатор прежний.
        var innerAgain = await context.AddRootAsync(Path.Combine("10S-417", "TCI2"));
        var afterReadd = await context.GetAsync(ApiRoutes.V1.Search + "?q=dnp3", DiskAppJsonContext.Default.SearchResponse);

        // Assert
        afterOuter.Items.ShouldHaveSingleItem().Id.ShouldBe(innerHit.Id, "файл внутреннего корня не дублируется внешним");
        inOuter.Items.ShouldHaveSingleItem().Id.ShouldBe(innerHit.Id, "область внешнего корня включает вложенный");
        readme.Items.ShouldHaveSingleItem();
        afterDelete.Items.ShouldHaveSingleItem().Id.ShouldBe(innerHit.Id);
        afterReadd.Items.ShouldHaveSingleItem().Id.ShouldBe(innerHit.Id);
        innerAgain.Id.ShouldNotBe(inner.Id);
        (await context.GetAsync(ApiRoutes.V1.Roots, DiskAppJsonContext.Default.IReadOnlyListRootDto)).Select(static root => root.Id).ShouldBe([outer.Id, innerAgain.Id]);
    }

    [Fact]
    public async Task PauseRescanResume_ControlsIndexing()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var root = await context.AddRootAsync();

        // Act
        var pause = await context.Client.PostAsync(new Uri(ApiRoutes.V1.Root(root.Id) + "/pause", UriKind.Relative), null, TestContext.Current.CancellationToken);
        context.Files.CreateFile("late.txt", "x");
        var rescan = await context.Client.PostAsync(new Uri(ApiRoutes.V1.Root(root.Id) + "/rescan", UriKind.Relative), null, TestContext.Current.CancellationToken);
        var paused = await context.GetAsync(ApiRoutes.V1.IndexStatus, DiskAppJsonContext.Default.IndexStatusDto);
        var resume = await context.Client.PostAsync(new Uri(ApiRoutes.V1.Root(root.Id) + "/resume", UriKind.Relative), null, TestContext.Current.CancellationToken);
        await context.WaitIdleAsync();
        var found = await context.GetAsync(ApiRoutes.V1.Search + "?q=late", DiskAppJsonContext.Default.SearchResponse);
        var unknown = await context.Client.PostAsync(new Uri(ApiRoutes.V1.Root(999) + "/pause", UriKind.Relative), null, TestContext.Current.CancellationToken);

        // Assert
        pause.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        rescan.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        paused.Roots.ShouldHaveSingleItem().Status.ShouldBe(RootStatus.Paused);
        resume.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        found.Items.ShouldHaveSingleItem().Name.ShouldBe("late.txt");
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PreviewExclusion_CountsEntriesUnderPattern()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        context.Files.CreateFile(Path.Combine("src", "app.cs"), "a");
        context.Files.CreateFile(Path.Combine("src", "build", "app.dll"), "b");
        context.Files.CreateFile(Path.Combine("src", "build", "app.pdb"), "c");
        context.Files.CreateFile(Path.Combine("docs", "notes.bak"), "d");
        await context.AddRootAsync();

        // Act
        var folder = await context.GetAsync(ApiRoutes.V1.IndexExclusionPreview + "?pattern=" + Uri.EscapeDataString("**/build/**"), DiskAppJsonContext.Default.ExclusionPreviewDto);
        var name = await context.GetAsync(ApiRoutes.V1.IndexExclusionPreview + "?pattern=*.bak", DiskAppJsonContext.Default.ExclusionPreviewDto);
        var nothing = await context.GetAsync(ApiRoutes.V1.IndexExclusionPreview + "?pattern=*.xyz", DiskAppJsonContext.Default.ExclusionPreviewDto);
        var empty = await context.Client.GetAsync(new Uri(ApiRoutes.V1.IndexExclusionPreview + "?pattern=%20", UriKind.Relative), TestContext.Current.CancellationToken);

        // Assert
        folder.Matched.ShouldBe(2, "файлы внутри build, сама папка build под шаблон **/build/** не подходит");
        folder.Complete.ShouldBeTrue();
        folder.Checked.ShouldBe(7, "4 файла и 3 папки");
        name.Matched.ShouldBe(1);
        nothing.Matched.ShouldBe(0);
        empty.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rebuild_ReindexesEverything()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        context.Files.CreateFile("a.txt", "a");
        context.Files.CreateFile("b.txt", "b");
        await context.AddRootAsync();

        // Act
        var response = await context.Client.PostAsync(new Uri(ApiRoutes.V1.IndexRebuild, UriKind.Relative), null, TestContext.Current.CancellationToken);
        await context.WaitIdleAsync();
        var search = await context.GetAsync(ApiRoutes.V1.Search + "?q=ext:txt", DiskAppJsonContext.Default.SearchResponse);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        search.Total.ShouldBe(2);
    }
}

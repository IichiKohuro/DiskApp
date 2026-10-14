using System.Net;
using System.Net.Http.Json;

using DiskApp.Contracts;
using DiskApp.Contracts.History;
using DiskApp.Contracts.Library;
using DiskApp.Contracts.Operations;
using DiskApp.Core.Operations;

using Microsoft.Extensions.DependencyInjection;

using FileHistoryService = DiskApp.Core.History.FileHistoryService;

namespace DiskApp.Server.Tests.Api;

/// <summary>
/// История файлов через API (PLAN.md, этап 20.6): перенос DiskApp в ленте и в истории файла, «Вернуть на место», смена
/// тегов, сводка первого сканирования; неизвестный вид события, чужой путь и несуществующее событие отклоняются.
/// </summary>
public sealed class FileHistoryApiTests
{
    [Fact]
    public async Task Move_InFeedAndFileHistory_RevertBringsBack()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var source = context.Files.CreateFile(Path.Combine("Загрузки", "акт.pdf"), "0123456789");
        var target = context.Files.CreateDirectory("417");
        await context.AddRootAsync();

        // Act
        await RunAsync(context, new OperationRequest { Kind = OperationKind.Move, Sources = [source], Destination = target });
        var moves = await context.GetAsync(ApiRoutes.V1.FileHistory + "?kinds=moved", DiskAppJsonContext.Default.FileHistoryPageDto);
        var summary = await context.GetAsync(ApiRoutes.V1.FileHistory + "?kinds=IndexSummary", DiskAppJsonContext.Default.FileHistoryPageDto);
        var moved = moves.Items.ShouldHaveSingleItem();
        var chain = await context.GetAsync(
            ApiRoutes.V1.FileHistoryOfPath + "?path=" + Uri.EscapeDataString(moved.Path),
            DiskAppJsonContext.Default.IReadOnlyListFileEventDto);
        using var revert = await context.Client.PostAsync(new Uri(ApiRoutes.V1.FileHistoryRevertOf(moved.Id), UriKind.Relative), null, TestContext.Current.CancellationToken);
        var reverted = await revert.Content.ReadFromJsonAsync(DiskAppJsonContext.Default.FileHistoryRevertDto, TestContext.Current.CancellationToken);
        await context.Host.Services.GetRequiredService<FileOperationService>().WhenIdleAsync(TestContext.Current.CancellationToken);

        // Assert
        (moved.Source, moved.OldPath, moved.Name, moved.Size, moved.CanRevert).ShouldBe((FileEventSource.DiskApp, source, "акт.pdf", 10L, true));
        moved.OperationId.ShouldNotBeNull();
        summary.Items.ShouldHaveSingleItem().Path.ShouldBe(context.Files.FullPath);
        chain.ShouldHaveSingleItem().Id.ShouldBe(moved.Id);
        revert.StatusCode.ShouldBe(HttpStatusCode.OK);
        reverted!.Operation.ShouldNotBeNull();
        File.Exists(source).ShouldBeTrue();
    }

    [Fact]
    public async Task SetTags_RecordedAsTagsChanged()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        context.Files.CreateFile("plan.md", "p");
        await context.AddRootAsync();
        var entry = (await context.GetAsync(ApiRoutes.V1.Search + "?q=plan", DiskAppJsonContext.Default.SearchResponse)).Items.ShouldHaveSingleItem();
        var work = await EntriesApiTests.CreateTagAsync(context, "работа");

        // Act
        using var response = await context.Client.PostAsJsonAsync(ApiRoutes.V1.Entry(entry.Id) + "/tags", new EntryTagsRequest { TagIds = [work.Id] },
            DiskAppJsonContext.Default.EntryTagsRequest, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var page = await context.GetAsync(ApiRoutes.V1.FileHistory + "?kinds=TagsChanged", DiskAppJsonContext.Default.FileHistoryPageDto);

        // Assert
        var tags = page.Items.ShouldHaveSingleItem();
        (tags.Name, tags.Details, tags.CanRevert).ShouldBe(("plan.md", "+работа", false));
    }

    [Fact]
    public async Task InvalidRequests_AreRejected()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        await context.AddRootAsync();

        // Act
        using var badKind = await context.Client.GetAsync(new Uri(ApiRoutes.V1.FileHistory + "?kinds=Moved,Exploded", UriKind.Relative), TestContext.Current.CancellationToken);
        using var outside = await context.Client.GetAsync(
            new Uri(ApiRoutes.V1.FileHistoryOfPath + "?path=" + Uri.EscapeDataString(context.Data.Resolve("x.txt")), UriKind.Relative),
            TestContext.Current.CancellationToken);
        using var missing = await context.Client.PostAsync(new Uri(ApiRoutes.V1.FileHistoryRevertOf(12345), UriKind.Relative), null, TestContext.Current.CancellationToken);
        var body = await badKind.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert
        badKind.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        body.ShouldContain("Exploded");
        outside.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // Ставит операцию, ждёт её и индекс, затем записывает накопленное историей.
    private static async Task RunAsync(ServerTestContext context, OperationRequest request)
    {
        using var response = await context.Client.PostAsJsonAsync(ApiRoutes.V1.Operations, request, DiskAppJsonContext.Default.OperationRequest, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        await context.Host.Services.GetRequiredService<FileOperationService>().WhenIdleAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await context.WaitIdleAsync();
        await context.Host.Services.GetRequiredService<FileHistoryService>().FlushAsync(TestContext.Current.CancellationToken);
    }
}

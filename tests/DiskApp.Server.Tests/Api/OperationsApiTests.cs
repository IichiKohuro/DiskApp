using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using DiskApp.Contracts;
using DiskApp.Contracts.Hub;
using DiskApp.Contracts.Operations;

using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace DiskApp.Server.Tests.Api;

/// <summary>
/// Файловые операции через API (PLAN.md, этапы 4.3, 4.5, 4.6): постановка и ход, конфликт с решением клиента,
/// отмена, возврат результата, удаление в корзину, загрузка, пути только внутри корней (в том числе через junction).
/// </summary>
public sealed class OperationsApiTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Copy_Accepted_CompletesAndUpdatesIndex()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var source = context.Files.CreateFile(Path.Combine("inbox", "contract.pdf"), "pdf");
        var destination = context.Files.CreateDirectory("archive");
        await context.AddRootAsync();

        // Act
        var response = await PostAsync(context, new OperationRequest { Kind = OperationKind.Copy, Sources = [source], Destination = destination });
        var queued = await response.Content.ReadFromJsonAsync(DiskAppJsonContext.Default.OperationDto, TestContext.Current.CancellationToken);
        var done = await WaitForStateAsync(context, queued!.Id, OperationState.Completed);
        await context.WaitIdleAsync();
        var search = await context.GetAsync(ApiRoutes.V1.Search + "?q=contract", DiskAppJsonContext.Default.SearchResponse);
        var all = await context.GetAsync(ApiRoutes.V1.Operations, DiskAppJsonContext.Default.IReadOnlyListOperationDto);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        response.Headers.Location!.ToString().ShouldBe(ApiRoutes.V1.Operation(queued.Id));
        done.Progress.ProcessedBytes.ShouldBe(3);
        File.Exists(Path.Combine(destination, "contract.pdf")).ShouldBeTrue();
        search.Total.ShouldBe(2, "копия попала в индекс без ожидания наблюдателя");
        all.ShouldHaveSingleItem().Id.ShouldBe(queued.Id);
    }

    [Fact]
    public async Task Enqueue_OutsideRootsOrInvalid_IsRejected()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var file = context.Files.CreateFile("a.txt", "a");
        await context.AddRootAsync();

        // Act
        var outside = await PostAsync(context, new OperationRequest { Kind = OperationKind.Copy, Sources = [file], Destination = context.Data.FullPath });
        var relative = await PostAsync(context, new OperationRequest { Kind = OperationKind.Delete, Sources = ["a.txt"] });
        var badName = await PostAsync(context, new OperationRequest { Kind = OperationKind.Rename, Sources = [file], NewName = "a?.txt" });
        var body = await badName.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        // Assert
        outside.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        relative.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        badName.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        body.ShouldContain("operation.invalid");
    }

    [Fact]
    public async Task Conflict_Ask_WaitsForDecisionThroughApiAndHub()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var source = context.Files.CreateFile(Path.Combine("src", "plan.md"), "new");
        context.Files.CreateFile(Path.Combine("dest", "plan.md"), "old");
        await context.AddRootAsync();
        var asked = new TaskCompletionSource<ConflictDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource<OperationCompletedDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(context.Host.BaseAddress!, HubRoutes.Path))
            .AddJsonProtocol(static json => json.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, DiskAppJsonContext.Default))
            .Build();
        hub.On<ConflictDto>(nameof(IDiskAppHubClient.OnOperationNeedsDecision), conflict => asked.TrySetResult(conflict));
        hub.On<OperationCompletedDto>(nameof(IDiskAppHubClient.OnOperationCompleted), done => completed.TrySetResult(done));
        await hub.StartAsync(TestContext.Current.CancellationToken);

        // Act
        var queued = await (await PostAsync(context, new OperationRequest { Kind = OperationKind.Copy, Sources = [source], Destination = context.Files.Resolve("dest") }))
            .Content.ReadFromJsonAsync(DiskAppJsonContext.Default.OperationDto, TestContext.Current.CancellationToken);
        var conflict = await asked.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        var status = await hub.InvokeAsync<HubStatusDto>(HubRoutes.GetStatus, TestContext.Current.CancellationToken);
        var decision = await context.Client.PostAsJsonAsync(ApiRoutes.V1.Operation(queued!.Id) + "/decision", new ConflictDecisionDto { Resolution = ConflictResolution.Overwrite },
            DiskAppJsonContext.Default.ConflictDecisionDto, TestContext.Current.CancellationToken);
        var finished = await completed.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        var again = await context.Client.PostAsJsonAsync(ApiRoutes.V1.Operation(queued.Id) + "/decision", new ConflictDecisionDto { Resolution = ConflictResolution.Skip },
            DiskAppJsonContext.Default.ConflictDecisionDto, TestContext.Current.CancellationToken);

        // Assert
        conflict.OperationId.ShouldBe(queued.Id);
        conflict.TargetPath.ShouldEndWith(Path.Combine("dest", "plan.md"));
        status.Operations.ShouldHaveSingleItem().State.ShouldBe(OperationState.WaitingDecision);
        decision.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        finished.State.ShouldBe(OperationState.Completed);
        context.Shell!.Calls.ShouldContain(call => call.Action == "recycle", "заменяемый файл отправлен в корзину");
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task RenameUndoCancel_FollowStateRules()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var file = context.Files.CreateFile("draft.txt", "d");
        await context.AddRootAsync();

        // Act
        var renamed = await (await PostAsync(context, new OperationRequest { Kind = OperationKind.Rename, Sources = [file], NewName = "final.txt" }))
            .Content.ReadFromJsonAsync(DiskAppJsonContext.Default.OperationDto, TestContext.Current.CancellationToken);
        var done = await WaitForStateAsync(context, renamed!.Id, OperationState.Completed);
        var cancelFinished = await context.Client.PostAsync(new Uri(ApiRoutes.V1.Operation(renamed.Id) + "/cancel", UriKind.Relative), null, TestContext.Current.CancellationToken);
        var undo = await context.Client.PostAsync(new Uri(ApiRoutes.V1.Operation(renamed.Id) + "/undo", UriKind.Relative), null, TestContext.Current.CancellationToken);
        var undoOperation = await undo.Content.ReadFromJsonAsync(DiskAppJsonContext.Default.OperationDto, TestContext.Current.CancellationToken);
        await WaitForStateAsync(context, undoOperation!.Id, OperationState.Completed);
        var undoAgain = await context.Client.PostAsync(new Uri(ApiRoutes.V1.Operation(renamed.Id) + "/undo", UriKind.Relative), null, TestContext.Current.CancellationToken);
        var unknown = await context.Client.GetAsync(new Uri(ApiRoutes.V1.Operation(Guid.NewGuid()), UriKind.Relative), TestContext.Current.CancellationToken);

        // Assert
        done.CanUndo.ShouldBeTrue();
        cancelFinished.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        undo.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        File.Exists(file).ShouldBeTrue("отмена вернула прежнее имя");
        undoAgain.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_GoesThroughRecycleBin()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var file = context.Files.CreateFile("old.log", "l");
        await context.AddRootAsync();

        // Act
        var queued = await (await PostAsync(context, new OperationRequest { Kind = OperationKind.Delete, Sources = [file] }))
            .Content.ReadFromJsonAsync(DiskAppJsonContext.Default.OperationDto, TestContext.Current.CancellationToken);
        await WaitForStateAsync(context, queued!.Id, OperationState.Completed);

        // Assert
        context.Shell!.Calls.ShouldContain(("recycle", file));
    }

    [Fact]
    public async Task Upload_SavesFilesWithConflictPolicyAndIndexesThem()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var folder = context.Files.CreateDirectory("uploads");
        await context.AddRootAsync();
        var uri = ApiRoutes.V1.Upload + "?destination=" + Uri.EscapeDataString(folder);

        // Act
        var first = await UploadAsync(context, uri, ("scan.png", "png-1"), ("notes.txt", "n"));
        var skipped = await UploadAsync(context, uri + "&conflicts=Skip", ("scan.png", "png-2"));
        var keepBoth = await UploadAsync(context, uri, ("scan.png", "png-3"));
        var outside = await context.Client.PostAsync(new Uri(ApiRoutes.V1.Upload + "?destination=" + Uri.EscapeDataString(context.Data.FullPath), UriKind.Relative),
            Multipart(("x.txt", "x")), TestContext.Current.CancellationToken);
        await context.WaitIdleAsync();
        var search = await context.GetAsync(ApiRoutes.V1.Search + "?q=scan", DiskAppJsonContext.Default.SearchResponse);

        // Assert
        first.Saved.Count.ShouldBe(2);
        File.ReadAllText(Path.Combine(folder, "scan.png")).ShouldBe("png-1");
        skipped.Skipped.ShouldBe(["scan.png"]);
        keepBoth.Saved.ShouldHaveSingleItem().ShouldBe(Path.Combine(folder, "scan (2).png"));
        outside.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        search.Total.ShouldBe(2);
    }

    [Fact]
    public async Task Junction_InsideRootPointingOutside_IsForbidden()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var outside = context.Data.CreateDirectory("secret");
        File.WriteAllText(Path.Combine(outside, "passwords.txt"), "x");
        await context.AddRootAsync();
        var link = context.Files.Resolve("link");
        if (!CreateJunction(link, outside))
        {
            Assert.Skip("Не удалось создать junction (mklink /J).");
        }

        // Act
        var browse = await context.Client.GetAsync(new Uri(ApiRoutes.V1.Browse + "?path=" + Uri.EscapeDataString(link), UriKind.Relative), TestContext.Current.CancellationToken);
        var copy = await PostAsync(context, new OperationRequest { Kind = OperationKind.Copy, Sources = [Path.Combine(link, "passwords.txt")], Destination = context.Files.FullPath });

        // Assert
        browse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        copy.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static Task<HttpResponseMessage> PostAsync(ServerTestContext context, OperationRequest request)
        => context.Client.PostAsJsonAsync(ApiRoutes.V1.Operations, request, DiskAppJsonContext.Default.OperationRequest, TestContext.Current.CancellationToken);

    private static async Task<OperationDto> WaitForStateAsync(ServerTestContext context, Guid id, OperationState state)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (true)
        {
            var operation = await context.GetAsync(ApiRoutes.V1.Operation(id), DiskAppJsonContext.Default.OperationDto);
            if (operation.State == state || DateTime.UtcNow > deadline)
            {
                operation.State.ShouldBe(state, operation.Message);
                return operation;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    private static async Task<UploadResponse> UploadAsync(ServerTestContext context, string uri, params (string Name, string Content)[] files)
    {
        using var response = await context.Client.PostAsync(new Uri(uri, UriKind.Relative), Multipart(files), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await response.Content.ReadFromJsonAsync(DiskAppJsonContext.Default.UploadResponse, TestContext.Current.CancellationToken))!;
    }

    private static MultipartFormDataContent Multipart(params (string Name, string Content)[] files)
    {
        var content = new MultipartFormDataContent();
        foreach (var (name, text) in files)
        {
            var part = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(text));
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(part, "files", name);
        }

        return content;
    }

    private static bool CreateJunction(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit(10_000);
        return process.ExitCode == 0 && Directory.Exists(link);
    }
}

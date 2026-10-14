using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

using DiskApp.Contracts;
using DiskApp.Contracts.Operations;
using DiskApp.Core.Operations;
using DiskApp.Core.Paths;

using Microsoft.Extensions.DependencyInjection;

using InboxService = DiskApp.Core.Sorting.InboxService;
using SortingHistory = DiskApp.Core.Sorting.SortingHistory;
using SortingService = DiskApp.Core.Sorting.SortingService;

namespace DiskApp.Server.Tests.Api;

/// <summary>
/// Подсказки рабочей папки по истории решений через API (PLAN.md, этап 17.4): переносы с «Разбора» учат подсказку, шаблон
/// рабочей папки сильнее истории, «оставить здесь» гасит подсказку для похожих файлов.
/// </summary>
public sealed class SortingSuggestionsApiTests
{
    private static readonly DateTime Old = DateTime.UtcNow.AddHours(-2);

    private static readonly string[] KeptReceipts = ["чек магазин.pdf", "чек кафе.pdf", "чек такси.pdf"];

    [Fact]
    public async Task Inbox_AfterUserMoves_SuggestsFolderByWord_TemplateWins()
    {
        // Arrange: два акта уже разложены в «417\Акты»; у TCI2 шаблон «план»
        await using var context = await ServerTestContext.StartAsync();
        var inbox = context.Files.CreateDirectory("Загрузки");
        var acts = context.Files.CreateDirectory(Path.Combine("417", "Акты"));
        var tci2 = context.Files.CreateDirectory("TCI2");
        foreach (var name in new[] { "акт 1.pdf", "акт 2.pdf", "акт 3.pdf", "акт план.pdf", "фото.jpg" })
        {
            Aged(context.Files.CreateFile(Path.Combine("Загрузки", name), "x"));
        }

        await context.AddRootAsync();
        await PatchAsync(context, inbox, new JsonObject { ["path"] = tci2, ["rules"] = new JsonArray(new JsonObject { ["pattern"] = "план", ["mode"] = "suggest" }) });
        await SortAsync(context, acts, Path.Combine(inbox, "акт 1.pdf"), Path.Combine(inbox, "акт 2.pdf"));

        // Act
        var queue = await context.GetAsync(ApiRoutes.V1.SortingInbox, DiskAppJsonContext.Default.InboxDto);

        // Assert
        var act = queue.Items.Single(static item => item.Entry.Name == "акт 3.pdf");
        act.Decision.ShouldBe(SortingDecision.None);
        act.Suggestion.ShouldBe(new SortingSuggestionDto(PathNormalizer.Current.Normalize(acts), SortingSuggestionReason.Word, 2, "акт"));
        var plan = queue.Items.Single(static item => item.Entry.Name == "акт план.pdf");
        plan.Decision.ShouldBe(SortingDecision.Suggest);
        plan.Suggestion.ShouldBeNull();
        queue.Items.Single(static item => item.Entry.Name == "фото.jpg").Suggestion!.Reason.ShouldBe(SortingSuggestionReason.Recent);
    }

    [Fact]
    public async Task Inbox_SimilarFilesKeptHere_NoSuggestion()
    {
        // Arrange: два чека перенесли в 417, три оставили в «Загрузках»
        await using var context = await ServerTestContext.StartAsync();
        var inbox = context.Files.CreateDirectory("Загрузки");
        var work = context.Files.CreateDirectory("417");
        foreach (var name in new[] { "чек 1.pdf", "чек 2.pdf", "чек магазин.pdf", "чек кафе.pdf", "чек такси.pdf", "чек аптека.pdf" })
        {
            Aged(context.Files.CreateFile(Path.Combine("Загрузки", name), "x"));
        }

        await context.AddRootAsync();
        await PatchAsync(context, inbox);
        await SortAsync(context, work, Path.Combine(inbox, "чек 1.pdf"), Path.Combine(inbox, "чек 2.pdf"));
        var before = await context.GetAsync(ApiRoutes.V1.SortingInbox, DiskAppJsonContext.Default.InboxDto);

        // Act
        using var kept = await context.Client.PutAsJsonAsync(
            ApiRoutes.V1.SortingInboxState,
            new SetInboxStateRequest { Paths = [.. KeptReceipts.Select(name => Path.Combine(inbox, name))], State = InboxState.Kept },
            DiskAppJsonContext.Default.SetInboxStateRequest,
            TestContext.Current.CancellationToken);
        kept.EnsureSuccessStatusCode();
        var after = await context.GetAsync(ApiRoutes.V1.SortingInbox, DiskAppJsonContext.Default.InboxDto);

        // Assert
        before.Items.Single(static item => item.Entry.Name == "чек аптека.pdf").Suggestion!.Reason.ShouldBe(SortingSuggestionReason.Word);
        after.Items.Single(static item => item.Entry.Name == "чек аптека.pdf").Suggestion.ShouldBeNull();
    }

    [Fact]
    public async Task AutoSort_DoesNotTeachSuggestions()
    {
        // Arrange: шаблон «417_*» сам переносит файл; перенос авторазбора — не решение пользователя
        await using var context = await ServerTestContext.StartAsync();
        var inbox = context.Files.CreateDirectory("Загрузки");
        var work = context.Files.CreateDirectory("417");
        Aged(context.Files.CreateFile(Path.Combine("Загрузки", "417_смета.xlsx"), "x"));
        await context.AddRootAsync();

        // Act
        await PatchAsync(context, inbox, new JsonObject { ["path"] = work, ["rules"] = new JsonArray(new JsonObject { ["pattern"] = "417_*", ["mode"] = "auto" }) });
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!File.Exists(Path.Combine(work, "417_смета.xlsx")) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        await context.Host.Services.GetRequiredService<FileOperationService>().WhenIdleAsync(TestContext.Current.CancellationToken);
        Aged(context.Files.CreateFile(Path.Combine("Загрузки", "смета.xlsx"), "x"));
        await context.WaitIdleAsync();
        var items = await context.Host.Services.GetRequiredService<InboxService>().GetAsync(TestContext.Current.CancellationToken);
        var suggestions = await context.Host.Services.GetRequiredService<SortingHistory>().SuggestAsync(items.Items, TestContext.Current.CancellationToken);

        // Assert
        File.Exists(Path.Combine(work, "417_смета.xlsx")).ShouldBeTrue();
        suggestions.ShouldAllBe(static suggestion => suggestion == null);
    }

    private static string Aged(string path)
    {
        File.SetLastWriteTimeUtc(path, Old);
        return path;
    }

    private static async Task PatchAsync(ServerTestContext context, string inbox, params JsonObject[] workFolders)
    {
        var sorting = new JsonObject
        {
            ["inboxes"] = new JsonArray(new JsonObject { ["path"] = inbox }),
            ["workFolders"] = new JsonArray([.. workFolders]),
            ["settleSeconds"] = 0,
        };
        using var patch = await context.Client.PatchAsync(
            new Uri(ApiRoutes.V1.Settings, UriKind.Relative),
            new StringContent(new JsonObject { ["sorting"] = sorting }.ToJsonString(), Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        patch.EnsureSuccessStatusCode();
    }

    // Перенос с «Разбора»: ждём операцию, теги и запись в историю решений.
    private static async Task SortAsync(ServerTestContext context, string destination, params string[] sources)
    {
        using var response = await context.Client.PostAsJsonAsync(ApiRoutes.V1.SortingMove, new SortFilesRequest { Sources = sources, Destination = destination }, DiskAppJsonContext.Default.SortFilesRequest, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var operation = (await response.Content.ReadFromJsonAsync(DiskAppJsonContext.Default.OperationDto, TestContext.Current.CancellationToken))!;
        await context.Host.Services.GetRequiredService<FileOperationService>().WhenIdleAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await context.Host.Services.GetRequiredService<SortingService>().WhenTaggedAsync(operation.Id)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await context.WaitIdleAsync();
    }
}

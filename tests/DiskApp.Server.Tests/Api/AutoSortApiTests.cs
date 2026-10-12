using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

using DiskApp.Contracts;
using DiskApp.Contracts.Operations;

namespace DiskApp.Server.Tests.Api;

/// <summary>
/// Авторазбор по шаблонам рабочих папок на настоящем сервере (PLAN.md, этап 17.5): пришедший файл, подходящий к шаблону
/// «переносить самим» одной папки, переносится операцией «авторазбор»; файл двух папок, папка, отложенный файл и шаблон
/// «предлагать первым» остаются с подсказкой; недокачанный ждёт «времени успокоения»; вернувшийся после отмены больше сам
/// не переносится. Сухой прогон показывает, что куда ушло бы, и ничего не трогает; неверные шаблоны отклоняются.
/// </summary>
public sealed class AutoSortApiTests
{
    private static readonly DateTime Old = DateTime.UtcNow.AddHours(-2);

    [Fact]
    public async Task AutoRule_ArrivedFile_IsMovedByAutoSortOperation()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var inbox = context.Files.CreateDirectory("Загрузки");
        var report = Aged(context.Files.CreateFile(Path.Combine("Загрузки", "417_ТЗ.pdf"), "т"));
        var photo = Aged(context.Files.CreateFile(Path.Combine("Загрузки", "фото.jpg"), "ф"));
        var work = context.Files.CreateDirectory(Path.Combine("Работа", "417"));
        await context.AddRootAsync();

        // Act
        await PatchAsync(context, inbox, Folder(work, Rule("417_*")));
        await WaitForFileAsync(Path.Combine(work, "417_ТЗ.pdf"));
        var operations = await context.GetAsync(ApiRoutes.V1.Operations, DiskAppJsonContext.Default.IReadOnlyListOperationDto);

        // Assert: неподходящий файл остался, перенос помечен «авторазбор»
        File.Exists(report).ShouldBeFalse();
        File.Exists(photo).ShouldBeTrue();
        var operation = operations.ShouldHaveSingleItem();
        operation.Origin.ShouldBe(OperationOrigin.AutoSort);
        operation.Destination.ShouldBe(work);
    }

    [Fact]
    public async Task AutoRule_TwoFoldersFolderAndPostponed_StayWithSuggestions()
    {
        // Arrange: «417_TCI2» подходит к двум папкам, «417_исходники» — папка, «417_потом» отложен, «417_акт» уйдёт сам
        await using var context = await ServerTestContext.StartAsync();
        var inbox = context.Files.CreateDirectory("Загрузки");
        var both = Aged(context.Files.CreateFile(Path.Combine("Загрузки", "417_TCI2.pdf"), "1"));
        var later = Aged(context.Files.CreateFile(Path.Combine("Загрузки", "417_потом.pdf"), "2"));
        var act = Aged(context.Files.CreateFile(Path.Combine("Загрузки", "417_акт.pdf"), "3"));
        var sources = context.Files.CreateDirectory(Path.Combine("Загрузки", "417_исходники"));
        Aged(context.Files.CreateFile(Path.Combine("Загрузки", "417_исходники", "main.c"), "4"));
        Directory.SetLastWriteTimeUtc(sources, Old);
        var work = context.Files.CreateDirectory("417");
        var tci = context.Files.CreateDirectory("TCI2");
        await context.AddRootAsync();
        await PatchAsync(context, inbox);
        await PutStateAsync(context, InboxState.Postponed, later);

        // Act
        await PatchAsync(context, inbox, Folder(work, Rule("417_*")), Folder(tci, Rule("TCI2", mode: "suggest")));
        await WaitForFileAsync(Path.Combine(work, "417_акт.pdf"));
        var queue = await context.GetAsync(ApiRoutes.V1.SortingInbox, DiskAppJsonContext.Default.InboxDto);

        // Assert
        File.Exists(act).ShouldBeFalse();
        File.Exists(both).ShouldBeTrue();
        Directory.Exists(sources).ShouldBeTrue();
        File.Exists(later).ShouldBeTrue();
        var choice = queue.Items.Single(static item => item.Entry.Name == "417_TCI2.pdf");
        choice.Decision.ShouldBe(SortingDecision.Choice);
        choice.Matches.Select(static match => match.WorkFolder).ShouldBe([work, tci], ignoreOrder: true);
        queue.Items.Single(static item => item.Entry.Name == "417_исходники").Decision.ShouldBe(SortingDecision.Suggest);
        queue.Items.Single(static item => item.Entry.Name == "417_потом.pdf").Decision.ShouldBe(SortingDecision.Suggest);
    }

    [Fact]
    public async Task AutoRule_FileStillWritten_WaitsForSettleTime()
    {
        // Arrange: свежий файл, «время успокоения» 5 с
        await using var context = await ServerTestContext.StartAsync();
        var inbox = context.Files.CreateDirectory("Загрузки");
        var work = context.Files.CreateDirectory("417");
        await context.AddRootAsync();
        await PatchAsync(context, inbox, settleSeconds: 5, [Folder(work, Rule("417"))]);
        var fresh = context.Files.CreateFile(Path.Combine("Загрузки", "Счёт 417.pdf"), "1");

        // Act
        await WaitForAsync(context, static queue => queue.Items.Any(static item => item.Entry.Name == "Счёт 417.pdf"));
        var early = await context.GetAsync(ApiRoutes.V1.SortingInbox, DiskAppJsonContext.Default.InboxDto);
        var stillThere = File.Exists(fresh);
        await WaitForFileAsync(Path.Combine(work, "Счёт 417.pdf"));

        // Assert: пока дописывается, файл на месте, но уже видно, что он уйдёт сам
        stillThere.ShouldBeTrue();
        var item = early.Items.Single(static item => item.Entry.Name == "Счёт 417.pdf");
        item.IsReady.ShouldBeFalse();
        item.Decision.ShouldBe(SortingDecision.Auto);
    }

    [Fact]
    public async Task AutoSort_Undone_FileStaysForManualSorting()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var inbox = context.Files.CreateDirectory("Загрузки");
        var report = Aged(context.Files.CreateFile(Path.Combine("Загрузки", "417_ТЗ.pdf"), "т"));
        var work = context.Files.CreateDirectory("417");
        await context.AddRootAsync();
        await PatchAsync(context, inbox, Folder(work, Rule("417_*")));
        await WaitForFileAsync(Path.Combine(work, "417_ТЗ.pdf"));
        var operation = (await context.GetAsync(ApiRoutes.V1.Operations, DiskAppJsonContext.Default.IReadOnlyListOperationDto)).ShouldHaveSingleItem();

        // Act: отмена возвращает файл; новый подходящий файл доказывает, что авторазбор после этого прошёл
        using var undo = await context.Client.PostAsync(new Uri($"{ApiRoutes.V1.Operations}/{operation.Id}/undo", UriKind.Relative), null, TestContext.Current.CancellationToken);
        await WaitForFileAsync(report);
        await WaitForAsync(context, static queue => queue.Items.Any(static item => item.Entry.Name == "417_ТЗ.pdf"));
        var next = Aged(context.Files.CreateFile(Path.Combine("Загрузки", "417_акт.pdf"), "а"));
        await WaitForFileAsync(Path.Combine(work, "417_акт.pdf"));
        await WaitForAsync(context, static queue => !queue.Items.Any(static item => item.Entry.Name == "417_акт.pdf"));
        var queue = await context.GetAsync(ApiRoutes.V1.SortingInbox, DiskAppJsonContext.Default.InboxDto);

        // Assert: вернувшийся — обычный новый для интерфейса, но сам больше не уходит
        undo.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        File.Exists(report).ShouldBeTrue();
        File.Exists(next).ShouldBeFalse();
        var returned = queue.Items.Single(static item => item.Entry.Name == "417_ТЗ.pdf");
        returned.State.ShouldBe(InboxState.New);
        returned.Decision.ShouldBe(SortingDecision.Suggest);
        queue.NewCount.ShouldBe(1);
    }

    [Fact]
    public async Task Preview_ProposedRules_ShowsPlanWithNamesAndMovesNothing()
    {
        // Arrange: шаблон имени «{папка}_{имя}», шаблоны — только в запросе, в настройках их нет
        await using var context = await ServerTestContext.StartAsync();
        var inbox = context.Files.CreateDirectory("Загрузки");
        var report = Aged(context.Files.CreateFile(Path.Combine("Загрузки", "ТЗ_417.docx"), "т"));
        var later = Aged(context.Files.CreateFile(Path.Combine("Загрузки", "акт 417.pdf"), "а"));
        Aged(context.Files.CreateFile(Path.Combine("Загрузки", "фото.jpg"), "ф"));
        var work = context.Files.CreateDirectory("Работа");
        context.Files.CreateDirectory(Path.Combine("Работа", "Акты"));
        await context.AddRootAsync();
        await PatchAsync(context, inbox, nameTemplate: "{папка}_{имя}");
        await PutStateAsync(context, InboxState.Postponed, later);
        var proposed = new JsonArray(Folder(work, Rule("417", extensions: ["docx", "PDF"]), Rule("акт*", subfolder: "Акты", mode: "suggest")));

        // Act
        var preview = await PreviewAsync(context, new JsonObject { ["workFolders"] = proposed });

        // Assert: «ТЗ_417» уйдёт сам с новым именем; отложенный «акт» подходит и к папке, и к подпапке «Акты» — выбор
        File.Exists(report).ShouldBeTrue();
        preview.Items.Count.ShouldBe(2);
        var moving = preview.Items.Single(static item => item.Name == "ТЗ_417.docx");
        moving.WillMove.ShouldBeTrue();
        moving.Decision.ShouldBe(SortingDecision.Auto);
        moving.NewName.ShouldBe("Работа_ТЗ_417.docx");
        var suggested = preview.Items.Single(static item => item.Name == "акт 417.pdf");
        suggested.WillMove.ShouldBeFalse();
        suggested.Matches.Select(static match => match.Destination).Distinct().Count().ShouldBe(2);
        suggested.Decision.ShouldBe(SortingDecision.Choice);
    }

    [Theory]
    [InlineData("{\"pattern\":\"Работа/417_*\"}", "pattern")]
    [InlineData("{\"pattern\":\"\"}", "pattern")]
    [InlineData("{\"pattern\":\"417\",\"subfolder\":\"..\\\\Чужая\"}", "subfolder")]
    [InlineData("{\"pattern\":\"417\",\"inbox\":\"Загрузки\"}", "inbox")]
    public async Task Rules_Invalid_AreRejectedBySettingsAndPreview(string rule, string field)
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var work = context.Files.CreateDirectory("417");
        var folder = new JsonObject { ["path"] = work, ["rules"] = new JsonArray(JsonNode.Parse(rule)) };
        var settings = new JsonObject { ["sorting"] = new JsonObject { ["workFolders"] = new JsonArray(folder.DeepClone()) } };

        // Act
        using var patch = await PatchJsonAsync(context, settings);
        using var preview = await context.Client.PostAsync(
            new Uri(ApiRoutes.V1.SortingRulesPreview, UriKind.Relative),
            new StringContent(new JsonObject { ["workFolders"] = new JsonArray(folder.DeepClone()) }.ToJsonString(), Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        // Assert
        patch.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await patch.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain($"sorting.workFolders[0].rules[0].{field}");
        preview.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static JsonObject Folder(string path, params JsonObject[] rules)
        => new() { ["path"] = path, ["rules"] = new JsonArray([.. rules]) };

    private static JsonObject Rule(string pattern, string mode = "auto", IReadOnlyList<string>? extensions = null, string? subfolder = null)
    {
        var rule = new JsonObject { ["pattern"] = pattern, ["mode"] = mode };
        if (extensions is not null)
        {
            rule["extensions"] = new JsonArray([.. extensions.Select(static extension => (JsonNode)JsonValue.Create(extension))]);
        }

        if (subfolder is not null)
        {
            rule["subfolder"] = subfolder;
        }

        return rule;
    }

    private static string Aged(string path)
    {
        File.SetLastWriteTimeUtc(path, Old);
        return path;
    }

    private static Task PatchAsync(ServerTestContext context, string inbox, params JsonObject[] workFolders)
        => PatchAsync(context, inbox, settleSeconds: 0, workFolders);

    private static Task PatchAsync(ServerTestContext context, string inbox, string nameTemplate)
        => PatchAsync(context, inbox, settleSeconds: 0, [], nameTemplate);

    private static async Task PatchAsync(ServerTestContext context, string inbox, int settleSeconds, JsonObject[] workFolders, string? nameTemplate = null)
    {
        var sorting = new JsonObject
        {
            ["inboxes"] = new JsonArray(new JsonObject { ["path"] = inbox }),
            ["workFolders"] = new JsonArray([.. workFolders]),
            ["settleSeconds"] = settleSeconds,
            ["nameTemplate"] = nameTemplate,
        };
        using var patch = await PatchJsonAsync(context, new JsonObject { ["sorting"] = sorting });
        patch.EnsureSuccessStatusCode();
    }

    private static Task<HttpResponseMessage> PatchJsonAsync(ServerTestContext context, JsonObject body)
        => context.Client.PatchAsync(new Uri(ApiRoutes.V1.Settings, UriKind.Relative), new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

    private static async Task PutStateAsync(ServerTestContext context, InboxState state, string path)
    {
        using var response = await context.Client.PutAsJsonAsync(ApiRoutes.V1.SortingInboxState, new SetInboxStateRequest { Paths = [path], State = state }, DiskAppJsonContext.Default.SetInboxStateRequest, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<SortingPreviewDto> PreviewAsync(ServerTestContext context, JsonObject body)
    {
        using var response = await context.Client.PostAsync(new Uri(ApiRoutes.V1.SortingRulesPreview, UriKind.Relative), new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(DiskAppJsonContext.Default.SortingPreviewDto, TestContext.Current.CancellationToken))!;
    }

    private static async Task WaitForFileAsync(string path)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!File.Exists(path) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        File.Exists(path).ShouldBeTrue($"{path} не появился за 20 с");
    }

    private static async Task WaitForAsync(ServerTestContext context, Func<InboxDto, bool> done)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!done(await context.GetAsync(ApiRoutes.V1.SortingInbox, DiskAppJsonContext.Default.InboxDto)) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}

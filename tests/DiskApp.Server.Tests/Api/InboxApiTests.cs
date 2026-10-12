using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

using DiskApp.Contracts;
using DiskApp.Contracts.Operations;

namespace DiskApp.Server.Tests.Api;

/// <summary>
/// Очередь папок-накопителей через API (PLAN.md, этап 17.1): «время успокоения», временные, служебные и скрытые файлы,
/// подпапки накопителя, состояния «отложен» и «оставить здесь», уборка записей пропавших файлов.
/// </summary>
public sealed class InboxApiTests
{
    private static readonly DateTime Old = DateTime.UtcNow.AddHours(-2);

    [Fact]
    public async Task Inbox_FileStillWritten_IsNotReadyUntilSettled()
    {
        // Arrange: старый файл пришёл давно, свежий ещё «дописывается» — время успокоения 60 с
        await using var context = await ServerTestContext.StartAsync();
        var inbox = context.Files.CreateDirectory("Загрузки");
        Aged(context.Files.CreateFile(Path.Combine("Загрузки", "старый.pdf"), "1"));
        context.Files.CreateFile(Path.Combine("Загрузки", "свежий.pdf"), "2");
        await context.AddRootAsync();
        await PatchSortingAsync(context, inbox, settleSeconds: 60);

        // Act
        var queue = await GetAsync(context);

        // Assert
        queue.Items.Single(static item => item.Entry.Name == "старый.pdf").IsReady.ShouldBeTrue();
        var fresh = queue.Items.Single(static item => item.Entry.Name == "свежий.pdf");
        fresh.IsReady.ShouldBeFalse();
        queue.NewCount.ShouldBe(1);
        queue.NextReadyUtc.ShouldBe(fresh.ReadyUtc);
        queue.Folders.ShouldHaveSingleItem().Problem.ShouldBe(InboxProblem.None);
    }

    [Fact]
    public async Task Inbox_TemporaryServiceAndHiddenFiles_AreLeftOut()
    {
        // Arrange: шаблоны по умолчанию плюс свой «*.log»; скрытый файл — по атрибуту
        await using var context = await ServerTestContext.StartAsync();
        var inbox = context.Files.CreateDirectory("Загрузки");
        foreach (var name in new[] { "договор.docx", "~$договор.docx", "desktop.ini", "Thumbs.db", "журнал.log", "скрытый.txt" })
        {
            Aged(context.Files.CreateFile(Path.Combine("Загрузки", name), "x"));
        }

        File.SetAttributes(Path.Combine(inbox, "скрытый.txt"), FileAttributes.Hidden);
        await context.AddRootAsync();
        await PatchSortingAsync(context, inbox, settleSeconds: 0, ignore: ["*.log", "~$*", "desktop.ini", "Thumbs.db"]);

        // Act
        var queue = await GetAsync(context);

        // Assert
        queue.Items.Select(static item => item.Entry.Name).ShouldBe(["договор.docx"]);
    }

    [Fact]
    public async Task Inbox_Subfolders_OnlyWhenIncluded()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var inbox = context.Files.CreateDirectory("Загрузки");
        Aged(context.Files.CreateFile(Path.Combine("Загрузки", "верх.pdf"), "1"));
        Aged(context.Files.CreateFile(Path.Combine("Загрузки", "Telegram", "фото.jpg"), "2"));
        await context.AddRootAsync();

        // Act
        await PatchSortingAsync(context, inbox, settleSeconds: 0);
        var direct = await GetAsync(context);
        await PatchSortingAsync(context, inbox, settleSeconds: 0, includeSubfolders: true);
        var deep = await GetAsync(context);

        // Assert: без подпапок папка «Telegram» — элемент накопителя; с подпапками — только файлы на любой глубине
        direct.Items.Select(static item => item.Entry.Name).ShouldBe(["верх.pdf", "Telegram"], ignoreOrder: true);
        deep.Items.Select(static item => item.Entry.Name).ShouldBe(["верх.pdf", "фото.jpg"], ignoreOrder: true);
    }

    [Fact]
    public async Task InboxState_PostponeKeepAndBack_ChangesCountAndPrunesGoneFiles()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var inbox = context.Files.CreateDirectory("Загрузки");
        var later = Aged(context.Files.CreateFile(Path.Combine("Загрузки", "потом.pdf"), "1"));
        var keep = Aged(context.Files.CreateFile(Path.Combine("Загрузки", "оставить.exe"), "2"));
        Aged(context.Files.CreateFile(Path.Combine("Загрузки", "разобрать.docx"), "3"));
        await context.AddRootAsync();
        await PatchSortingAsync(context, inbox, settleSeconds: 0);

        // Act
        (await PutAsync(context, InboxState.Postponed, later)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await PutAsync(context, InboxState.Kept, keep.ToUpperInvariant())).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var marked = await GetAsync(context);
        (await PutAsync(context, InboxState.New, later)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        File.Delete(keep);
        await WaitForAsync(context, static queue => !queue.Items.Any(static item => item.Entry.Name == "оставить.exe"));
        var afterward = await GetAsync(context);
        File.WriteAllText(keep, "снова");
        Aged(keep);
        await WaitForAsync(context, static queue => queue.Items.Any(static item => item.Entry.Name == "оставить.exe"));
        var returned = await GetAsync(context);

        // Assert: удалённый файл потерял запись — вернувшийся с тем же именем снова новый
        marked.Items.Single(static item => item.Entry.Name == "потом.pdf").State.ShouldBe(InboxState.Postponed);
        marked.Items.Single(static item => item.Entry.Name == "оставить.exe").State.ShouldBe(InboxState.Kept);
        marked.NewCount.ShouldBe(1);
        afterward.NewCount.ShouldBe(2);
        returned.Items.Single(static item => item.Entry.Name == "оставить.exe").State.ShouldBe(InboxState.New);
    }

    [Fact]
    public async Task InboxState_PathOutsideInboxes_IsRejected()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var inbox = context.Files.CreateDirectory("Загрузки");
        var work = context.Files.CreateFile(Path.Combine("Работа", "план.docx"), "1");
        await context.AddRootAsync();
        await PatchSortingAsync(context, inbox, settleSeconds: 0);

        // Act
        using var outside = await PutAsync(context, InboxState.Kept, work);
        using var empty = await PutAsync(context, InboxState.Kept);

        // Assert
        outside.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        empty.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Inbox_OutsideIndexAndMissing_AreReportedAsProblems()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        using var outside = new TemporaryDirectory("inbox-outside");
        await context.AddRootAsync("Корень");
        var missing = context.Files.Resolve(Path.Combine("Корень", "Нет"));

        // Act
        await PatchSortingAsync(context, [outside.FullPath, missing], settleSeconds: 0);
        var queue = await GetAsync(context);

        // Assert
        queue.Folders.Select(static folder => folder.Problem).ShouldBe([InboxProblem.OutsideIndex, InboxProblem.Missing]);
        queue.Items.ShouldBeEmpty();
    }

    private static string Aged(string path)
    {
        File.SetLastWriteTimeUtc(path, Old);
        return path;
    }

    private static Task<InboxDto> GetAsync(ServerTestContext context) => context.GetAsync(ApiRoutes.V1.SortingInbox, DiskAppJsonContext.Default.InboxDto);

    private static Task<HttpResponseMessage> PutAsync(ServerTestContext context, InboxState state, params string[] paths)
        => context.Client.PutAsJsonAsync(ApiRoutes.V1.SortingInboxState, new SetInboxStateRequest { Paths = paths, State = state }, DiskAppJsonContext.Default.SetInboxStateRequest, TestContext.Current.CancellationToken);

    private static Task PatchSortingAsync(ServerTestContext context, string inbox, int settleSeconds, IReadOnlyList<string>? ignore = null, bool includeSubfolders = false)
        => PatchSortingAsync(context, [inbox], settleSeconds, ignore, includeSubfolders);

    private static async Task PatchSortingAsync(ServerTestContext context, IReadOnlyList<string> inboxes, int settleSeconds, IReadOnlyList<string>? ignore = null, bool includeSubfolders = false)
    {
        var sorting = new JsonObject
        {
            ["inboxes"] = new JsonArray([.. inboxes.Select(path => (JsonNode)new JsonObject { ["path"] = path, ["includeSubfolders"] = includeSubfolders })]),
            ["settleSeconds"] = settleSeconds,
        };
        if (ignore is not null)
        {
            sorting["ignorePatterns"] = new JsonArray([.. ignore.Select(static pattern => (JsonNode)JsonValue.Create(pattern))]);
        }

        var json = new JsonObject { ["sorting"] = sorting }.ToJsonString();
        using var patch = await context.Client.PatchAsync(new Uri(ApiRoutes.V1.Settings, UriKind.Relative), new StringContent(json, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        patch.EnsureSuccessStatusCode();
    }

    private static async Task WaitForAsync(ServerTestContext context, Func<InboxDto, bool> done)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (!done(await GetAsync(context)) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }
}

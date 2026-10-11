using System.Net;
using System.Net.Http.Json;

using DiskApp.Contracts;
using DiskApp.Contracts.Operations;
using DiskApp.Core.Operations;
using DiskApp.Core.Repositories;
using DiskApp.Core.Sorting;

using Microsoft.Extensions.DependencyInjection;

namespace DiskApp.Server.Tests.Api;

/// <summary>
/// Разбор через API (PLAN.md, этап 17): перенос из накопителя в рабочую папку и новую подпапку с тегами, прежние теги
/// сохраняются и при переносе между корнями, поиск находит файлы на новом месте; пути вне корней, неверное имя
/// подпапки и перенос папки в саму себя отклоняются.
/// </summary>
public sealed class SortingApiTests
{
    [Fact]
    public async Task Move_ToNewSubfolderWithTags_MovesAndTagsKeepingPreviousTags()
    {
        // Arrange: накопитель и рабочая папка в одном корне, у одного файла уже есть тег
        await using var context = await ServerTestContext.StartAsync();
        var report = context.Files.CreateFile(Path.Combine("Загрузки", "ТЗ 417.pdf"), "t");
        var letter = context.Files.CreateFile(Path.Combine("Загрузки", "письмо.docx"), "l");
        var work = context.Files.CreateDirectory(Path.Combine("Работа", "417"));
        await context.AddRootAsync();
        await TagAsync(context, "письмо", "важное");

        // Act
        var response = await SortAsync(context, new SortFilesRequest { Sources = [report, letter], Destination = work, NewFolder = "Переписка", Tags = ["заказчик"] });
        await WaitAsync(context, response);
        var tagged = await context.GetAsync(ApiRoutes.V1.Search + "?q=" + Uri.EscapeDataString("tag:заказчик"), DiskAppJsonContext.Default.SearchResponse);
        var previous = await context.GetAsync(ApiRoutes.V1.Search + "?q=" + Uri.EscapeDataString("tag:важное"), DiskAppJsonContext.Default.SearchResponse);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var folder = Path.Combine(work, "Переписка");
        File.Exists(Path.Combine(folder, "ТЗ 417.pdf")).ShouldBeTrue();
        File.Exists(Path.Combine(folder, "письмо.docx")).ShouldBeTrue();
        File.Exists(report).ShouldBeFalse();
        tagged.Items.Select(static item => item.FullPath).ShouldBe([Path.Combine(folder, "ТЗ 417.pdf"), Path.Combine(folder, "письмо.docx")], ignoreOrder: true);
        previous.Items.ShouldHaveSingleItem().FullPath.ShouldBe(Path.Combine(folder, "письмо.docx"));
    }

    [Fact]
    public async Task Move_BetweenRoots_KeepsPreviousTagsOnNewEntry()
    {
        // Arrange: накопитель и рабочая папка — разные корни, перенос создаёт новую запись индекса
        await using var context = await ServerTestContext.StartAsync();
        var file = context.Files.CreateFile(Path.Combine("inbox", "схема.png"), "s");
        var work = context.Files.CreateDirectory("Soft");
        await context.AddRootAsync("inbox");
        await context.AddRootAsync("Soft");
        await TagAsync(context, "схема", "чертёж");

        // Act
        var response = await SortAsync(context, new SortFilesRequest { Sources = [file], Destination = work });
        await WaitAsync(context, response);
        var found = await context.GetAsync(ApiRoutes.V1.Search + "?q=" + Uri.EscapeDataString("tag:чертёж"), DiskAppJsonContext.Default.SearchResponse);

        // Assert
        found.Items.ShouldHaveSingleItem().FullPath.ShouldBe(Path.Combine(work, "схема.png"));
    }

    [Fact]
    public async Task Move_WithNames_RenamesKeepingTags()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var report = context.Files.CreateFile(Path.Combine("Загрузки", "ТЗ 417.docx"), "t");
        var photo = context.Files.CreateFile(Path.Combine("Загрузки", "фото.jpg"), "p");
        var work = context.Files.CreateDirectory("417");
        await context.AddRootAsync();

        // Act
        var response = await SortAsync(context, new SortFilesRequest
        {
            Sources = [report, photo],
            Destination = work,
            Names = ["2026-10-11_ТЗ 417.docx", null],
            Tags = ["заказчик"],
        });
        await WaitAsync(context, response);
        var tagged = await context.GetAsync(ApiRoutes.V1.Search + "?q=" + Uri.EscapeDataString("tag:заказчик"), DiskAppJsonContext.Default.SearchResponse);

        // Assert
        File.Exists(Path.Combine(work, "2026-10-11_ТЗ 417.docx")).ShouldBeTrue();
        File.Exists(Path.Combine(work, "фото.jpg")).ShouldBeTrue("без имени — прежнее");
        tagged.Items.Select(static item => item.Name).ShouldBe(["2026-10-11_ТЗ 417.docx", "фото.jpg"], ignoreOrder: true);
    }

    [Theory]
    [InlineData("bad|name.txt")]
    [InlineData("CON.txt")]
    public async Task Move_InvalidName_Returns400(string name)
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var file = context.Files.CreateFile(Path.Combine("in", "a.txt"), "a");
        var work = context.Files.CreateDirectory("work");
        await context.AddRootAsync();

        // Act
        var response = await SortAsync(context, new SortFilesRequest { Sources = [file], Destination = work, Names = [name] });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        File.Exists(file).ShouldBeTrue();
    }

    [Fact]
    public async Task Move_NamesCountMismatch_Returns400()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var file = context.Files.CreateFile(Path.Combine("in", "a.txt"), "a");
        var work = context.Files.CreateDirectory("work");
        await context.AddRootAsync();

        // Act
        var response = await SortAsync(context, new SortFilesRequest { Sources = [file], Destination = work, Names = ["x.txt", "y.txt"] });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Move_OutsideRoots_Returns403()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var file = context.Files.CreateFile("a.txt", "a");
        await context.AddRootAsync();
        var outside = context.Data.CreateDirectory("outside");

        // Act
        var response = await SortAsync(context, new SortFilesRequest { Sources = [file], Destination = outside });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        File.Exists(file).ShouldBeTrue();
    }

    [Theory]
    [InlineData("bad|name")]
    [InlineData("CON")]
    [InlineData("..")]
    public async Task Move_InvalidNewFolder_Returns400(string name)
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var file = context.Files.CreateFile(Path.Combine("in", "a.txt"), "a");
        var work = context.Files.CreateDirectory("work");
        await context.AddRootAsync();

        // Act
        var response = await SortAsync(context, new SortFilesRequest { Sources = [file], Destination = work, NewFolder = name });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        File.Exists(file).ShouldBeTrue();
    }

    [Fact]
    public async Task Move_FolderIntoItsSubfolder_Returns400()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var folder = context.Files.CreateDirectory("projects");
        var inner = context.Files.CreateDirectory(Path.Combine("projects", "inner"));
        await context.AddRootAsync();

        // Act
        var response = await SortAsync(context, new SortFilesRequest { Sources = [folder], Destination = inner });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static Task<HttpResponseMessage> SortAsync(ServerTestContext context, SortFilesRequest request)
        => context.Client.PostAsJsonAsync(ApiRoutes.V1.SortingMove, request, DiskAppJsonContext.Default.SortFilesRequest, TestContext.Current.CancellationToken);

    private static async Task WaitAsync(ServerTestContext context, HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var operation = (await response.Content.ReadFromJsonAsync(DiskAppJsonContext.Default.OperationDto, TestContext.Current.CancellationToken))!;
        await context.Host.Services.GetRequiredService<FileOperationService>().WhenIdleAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await context.Host.Services.GetRequiredService<SortingService>().WhenTaggedAsync(operation.Id)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await context.WaitIdleAsync();
    }

    private static async Task TagAsync(ServerTestContext context, string query, string tagName)
    {
        var entry = (await context.GetAsync(ApiRoutes.V1.Search + "?q=" + Uri.EscapeDataString(query), DiskAppJsonContext.Default.SearchResponse)).Items.ShouldHaveSingleItem();
        var tags = context.Host.Services.GetRequiredService<ITagRepository>();
        var tag = await tags.AddAsync(tagName, cancellationToken: TestContext.Current.CancellationToken);
        await tags.AssignAsync(entry.Id, tag.Id, TestContext.Current.CancellationToken);
    }
}

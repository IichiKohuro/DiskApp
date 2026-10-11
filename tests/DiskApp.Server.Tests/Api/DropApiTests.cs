using System.Net;
using System.Net.Http.Json;
using System.Text;

using DiskApp.Contracts;
using DiskApp.Contracts.Operations;
using DiskApp.Core.Operations;
using DiskApp.Server.Endpoints;

using Microsoft.Extensions.DependencyInjection;

namespace DiskApp.Server.Tests.Api;

/// <summary>
/// Бросок в папку-накопитель через API (PLAN.md, этап 18.2, ADR-0011): источник может лежать вне корней, назначение —
/// только накопитель из настроек; перенос, копия с «оставить оба» и отмена копии; запрос — только с этого компьютера.
/// </summary>
public sealed class DropApiTests
{
    [Fact]
    public async Task Drop_Move_FromOutsideRootsIntoInbox()
    {
        // Arrange: «Рабочий стол» вне корней, «Загрузки» — корень и накопитель
        await using var context = await ServerTestContext.StartAsync();
        var file = context.Files.CreateFile(Path.Combine("Рабочий стол", "счёт.pdf"), "s");
        var inbox = context.Files.CreateDirectory("Загрузки");
        await context.AddRootAsync("Загрузки");
        await SetInboxAsync(context, inbox);

        // Act
        var response = await DropAsync(context, new DropFilesRequest { Sources = [file], Destination = inbox });
        await WaitAsync(context, response);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        File.Exists(Path.Combine(inbox, "счёт.pdf")).ShouldBeTrue();
        File.Exists(file).ShouldBeFalse("перенос, а не копия");
    }

    [Fact]
    public async Task Drop_Copy_KeepsSourceKeepsBothAndUndoRecyclesCopy()
    {
        // Arrange: в накопителе уже лежит файл с тем же именем
        await using var context = await ServerTestContext.StartAsync();
        var file = context.Files.CreateFile(Path.Combine("Флешка", "фото.jpg"), "new");
        var inbox = context.Files.CreateDirectory("Загрузки");
        var existing = context.Files.CreateFile(Path.Combine("Загрузки", "фото.jpg"), "old");
        await context.AddRootAsync("Загрузки");
        await SetInboxAsync(context, inbox);

        // Act
        var response = await DropAsync(context, new DropFilesRequest { Sources = [file], Destination = inbox, Copy = true });
        var operation = await WaitAsync(context, response);
        var copy = Path.Combine(inbox, "фото (2).jpg");
        var copied = File.Exists(copy);
        var undo = await context.Client.PostAsync(ApiRoutes.V1.Operation(operation.Id) + "/undo", content: null, TestContext.Current.CancellationToken);
        await context.Host.Services.GetRequiredService<FileOperationService>().WhenIdleAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // Assert
        copied.ShouldBeTrue("одноимённый файл — «оставить оба»");
        File.Exists(file).ShouldBeTrue("копия не трогает источник");
        File.ReadAllText(existing).ShouldBe("old");
        undo.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        context.Shell!.Calls.ShouldContain(("recycle", copy), "отмена копии — в корзину");
    }

    [Fact]
    public async Task Drop_DestinationIsNotInbox_Returns403()
    {
        // Arrange: папка в корне, но не накопитель
        await using var context = await ServerTestContext.StartAsync();
        var file = context.Files.CreateFile(Path.Combine("Рабочий стол", "a.txt"), "a");
        var work = context.Files.CreateDirectory(Path.Combine("Корень", "Работа"));
        await context.AddRootAsync("Корень");

        // Act
        var response = await DropAsync(context, new DropFilesRequest { Sources = [file], Destination = work });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        File.Exists(file).ShouldBeTrue();
    }

    [Fact]
    public async Task Drop_MissingSource_Returns404()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var inbox = context.Files.CreateDirectory("Загрузки");
        await context.AddRootAsync("Загрузки");
        await SetInboxAsync(context, inbox);

        // Act
        var response = await DropAsync(context, new DropFilesRequest { Sources = [context.Files.Resolve("нет-такого.txt")], Destination = inbox });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("192.168.1.5", false)]
    [InlineData(null, false)]
    public void IsThisComputer_OnlyLoopback(string? address, bool expected)
        => SortingEndpoints.IsThisComputer(address is null ? null : IPAddress.Parse(address)).ShouldBe(expected);

    private static async Task SetInboxAsync(ServerTestContext context, string inbox)
    {
        var json = "{\"sorting\":{\"inboxes\":[{\"path\":" + System.Text.Json.JsonSerializer.Serialize(inbox) + "}]}}";
        using var response = await context.Client.PatchAsync(
            new Uri(ApiRoutes.V1.Settings, UriKind.Relative),
            new StringContent(json, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static Task<HttpResponseMessage> DropAsync(ServerTestContext context, DropFilesRequest request)
        => context.Client.PostAsJsonAsync(ApiRoutes.V1.SortingDrop, request, DiskAppJsonContext.Default.DropFilesRequest, TestContext.Current.CancellationToken);

    private static async Task<OperationDto> WaitAsync(ServerTestContext context, HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var operation = (await response.Content.ReadFromJsonAsync(DiskAppJsonContext.Default.OperationDto, TestContext.Current.CancellationToken))!;
        await context.Host.Services.GetRequiredService<FileOperationService>().WhenIdleAsync(TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        return operation;
    }
}

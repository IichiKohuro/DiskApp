using System.Net;
using System.Net.Http.Json;

using DiskApp.Contracts;
using DiskApp.Contracts.Entries;

namespace DiskApp.Server.Tests.Api;

/// <summary>
/// Записи по списку путей (PLAN.md, этап 8.7): порядок запроса, без повторов, пути вне корней и неизвестные — в Missing,
/// предел числа путей.
/// </summary>
public sealed class ResolveApiTests
{
    [Fact]
    public async Task Resolve_KnownMissingOutsideAndDuplicate_SplitsAndKeepsOrder()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        context.Files.CreateFile(Path.Combine("docs", "b.txt"), "b");
        context.Files.CreateFile(Path.Combine("docs", "a.txt"), "a");
        await context.AddRootAsync();
        var b = context.Files.Resolve(Path.Combine("docs", "b.txt"));
        var a = context.Files.Resolve(Path.Combine("docs", "a.txt"));
        var folder = context.Files.Resolve("docs");
        var unknown = context.Files.Resolve(Path.Combine("docs", "нет.txt"));
        var outside = context.Data.Resolve("чужой.txt");
        var request = new ResolveEntriesRequest { Paths = [b, unknown, a, outside, b.ToUpperInvariant(), folder, context.Files.FullPath] };

        // Act
        var response = await context.Client.PostAsJsonAsync(new Uri(ApiRoutes.V1.ResolveEntries, UriKind.Relative), request, DiskAppJsonContext.Default.ResolveEntriesRequest, TestContext.Current.CancellationToken);
        var result = await response.Content.ReadFromJsonAsync(DiskAppJsonContext.Default.ResolveEntriesResponse, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        result!.Entries.Select(static entry => entry.Name).ShouldBe(["b.txt", "a.txt", "docs"]);
        result.Entries[2].IsDirectory.ShouldBeTrue();
        result.Missing.ShouldBe([unknown, outside, context.Files.FullPath], "сам корень — не запись индекса");
    }

    [Fact]
    public async Task Resolve_TooManyPaths_ReturnsBadRequest()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        var request = new ResolveEntriesRequest { Paths = [.. Enumerable.Range(0, ResolveEntriesRequest.MaxPaths + 1).Select(static index => $@"C:\f{index}.txt")] };

        // Act
        var response = await context.Client.PostAsJsonAsync(new Uri(ApiRoutes.V1.ResolveEntries, UriKind.Relative), request, DiskAppJsonContext.Default.ResolveEntriesRequest, TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}

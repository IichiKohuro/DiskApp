using System.Text;

using DiskApp.Contracts;

namespace DiskApp.Server.Tests.Api;

/// <summary>
/// Путь рабочей папки следует за переименованием на диске (PLAN.md, этап 17.2): сервер с наблюдателем видит
/// переименование, служба слежения правит настройки, их отдаёт API.
/// </summary>
public sealed class SortingFolderTrackingApiTests
{
    [Fact]
    public async Task RenameOnDisk_UpdatesWorkFolderPathInSettings()
    {
        // Arrange: рабочая папка внутри корня, переименование — как в Проводнике, мимо DiskApp
        await using var context = await ServerTestContext.StartAsync();
        var work = context.Files.CreateDirectory(Path.Combine("Работа", "417"));
        await context.AddRootAsync();
        await context.WaitIdleAsync();
        var json = "{\"sorting\":{\"workFolders\":[{\"path\":" + System.Text.Json.JsonSerializer.Serialize(work) + ",\"label\":\"Проект\"}]}}";
        using (var patch = await context.Client.PatchAsync(new Uri(ApiRoutes.V1.Settings, UriKind.Relative), new StringContent(json, Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken))
        {
            patch.EnsureSuccessStatusCode();
        }

        var renamed = context.Files.Resolve(Path.Combine("Работа", "417 ТЗ"));

        // Act
        Directory.Move(work, renamed);
        var path = await WaitForPathAsync(context, renamed);

        // Assert
        path.ShouldBe(renamed);
    }

    private static async Task<string?> WaitForPathAsync(ServerTestContext context, string expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        string? path = null;
        while (DateTime.UtcNow < deadline)
        {
            var settings = await context.GetAsync(ApiRoutes.V1.Settings, DiskAppJsonContext.Default.SettingsDto);
            path = settings.Sorting.WorkFolders.Single().Path;
            if (string.Equals(path, expected, StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        return path;
    }
}

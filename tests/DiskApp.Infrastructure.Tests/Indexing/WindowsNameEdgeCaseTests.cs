namespace DiskApp.Infrastructure.Tests.Indexing;

/// <summary>
/// Имена, которые обычные функции Windows искажают или запрещают (PLAN.md, этап 10.3): точка и пробел в конце,
/// имена устройств. Такие файлы приходят с Linux, macOS и сетевых дисков; индекс должен видеть их как на диске
/// и не терять при сверке.
/// </summary>
public sealed class WindowsNameEdgeCaseTests
{
    private static readonly string[] Names = ["точка в конце.", "пробел в конце ", "CON.txt", "aux"];

    [Fact]
    public async Task ScanThenReconcile_NamesWin32Normalizes_IndexedAndStable()
    {
        // Arrange: создать такие имена можно только через префикс \\?\, который отключает нормализацию путей
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Нормализация имён — особенность Windows.");
        await using var context = await IndexingTestContext.CreateAsync();
        foreach (var name in Names)
        {
            await File.WriteAllTextAsync(Extended(Path.Combine(context.Files.FullPath, name)), name, TestContext.Current.CancellationToken);
        }

        try
        {
            // Act
            await context.FullScanAsync();
            var afterScan = context.IndexSnapshot();
            await context.ReconcileAsync();
            var afterReconcile = context.IndexSnapshot();

            // Assert
            Names.ShouldAllBe(name => afterScan.ContainsKey(name));
            afterReconcile.ShouldBe(afterScan, ignoreOrder: true);
        }
        finally
        {
            foreach (var name in Names)
            {
                File.Delete(Extended(Path.Combine(context.Files.FullPath, name)));
            }
        }
    }

    private static string Extended(string path) => @"\\?\" + path;
}

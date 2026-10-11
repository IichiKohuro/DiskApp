using System.IO.Compression;
using System.Text;

using DiskApp.Infrastructure.Diagnostics;

namespace DiskApp.Infrastructure.Tests.Diagnostics;

/// <summary>
/// Диагностический архив (PLAN.md, этап 9.5): последние файлы журнала (даже открытый на запись), настройки без
/// секретов, окружение.
/// </summary>
public sealed class DiagnosticArchiveTests : IDisposable
{
    private readonly TemporaryDirectory _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task Create_TakesLatestLogsSettingsAndEnvironment()
    {
        // Arrange
        var logs = Directory.CreateDirectory(Path.Combine(_folder.FullPath, "logs")).FullName;
        for (var day = 1; day <= 4; day++)
        {
            var log = Path.Combine(logs, $"diskapp-2026100{day}.log");
            await File.WriteAllTextAsync(log, $"день {day}", TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(log, new DateTime(2026, 10, day, 12, 0, 0, DateTimeKind.Utc));
        }

        var settings = Path.Combine(_folder.FullPath, "settings.json");
        await File.WriteAllTextAsync(settings, """{"appearance":{"theme":"Dark"},"server":{"sessionToken":"abc","port":1},"backup":[{"password":"p"}]}""", TestContext.Current.CancellationToken);
        var output = Path.Combine(_folder.FullPath, "out", "diag.zip");

        // Самый новый журнал открыт на запись, как у Serilog.
        await using var writer = new FileStream(Path.Combine(logs, "diskapp-20261004.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        // Act
        var path = await DiagnosticArchive.CreateAsync(
            new DiagnosticArchiveRequest(output, logs, settings, new Dictionary<string, string> { ["Версия"] = "0.5.0" }) { MaxLogFiles = 3 },
            TestContext.Current.CancellationToken);

        // Assert
        path.ShouldBe(output);
        using var zip = ZipFile.OpenRead(output);
        zip.Entries.Select(static entry => entry.FullName).ShouldBe(
            ["environment.txt", "settings.json", "logs/diskapp-20261004.log", "logs/diskapp-20261003.log", "logs/diskapp-20261002.log"]);
        Read(zip, "environment.txt").ShouldContain("Версия: 0.5.0");
        var redacted = Read(zip, "settings.json");
        redacted.ShouldContain("\"theme\": \"Dark\"");
        redacted.ShouldNotContain("abc");
        redacted.ShouldNotContain("\"p\"");
        redacted.ShouldContain("\"port\": 1");
        Read(zip, "logs/diskapp-20261004.log").ShouldBe("день 4");
        File.Exists(output + ".tmp").ShouldBeFalse();
    }

    [Fact]
    public async Task Create_LargeLog_KeepsTail()
    {
        // Arrange
        var logs = Directory.CreateDirectory(Path.Combine(_folder.FullPath, "logs")).FullName;
        await File.WriteAllTextAsync(Path.Combine(logs, "big.log"), new string('a', 100) + "КОНЕЦ", TestContext.Current.CancellationToken);
        var output = Path.Combine(_folder.FullPath, "diag.zip");

        // Act
        await DiagnosticArchive.CreateAsync(
            new DiagnosticArchiveRequest(output, logs, null, new Dictionary<string, string>()) { MaxLogBytes = Encoding.UTF8.GetByteCount("КОНЕЦ") },
            TestContext.Current.CancellationToken);

        // Assert
        using var zip = ZipFile.OpenRead(output);
        Read(zip, "logs/big.log").ShouldBe("КОНЕЦ");
        zip.GetEntry("settings.json").ShouldBeNull();
    }

    [Fact]
    public void RedactSettings_BrokenJson_DoesNotLeakContent()
    {
        // Act
        var text = DiagnosticArchive.RedactSettings("""{"sessionToken":"abc" """);

        // Assert
        text.ShouldStartWith("settings.json не читается");
        text.ShouldNotContain("abc");
    }

    private static string Read(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

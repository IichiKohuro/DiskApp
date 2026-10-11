using DiskApp.Client;
using DiskApp.Core.Abstractions;
using DiskApp.Desktop.Shell;
using DiskApp.UI.Services;

using Serilog;

namespace DiskApp.Desktop.Tests.Shell;

/// <summary>
/// Обслуживание данных (PLAN.md, этап 9.1): размеры базы и кэша, очистка кэша без касания базы, список сторонних
/// компонентов из ресурса сборки.
/// </summary>
public sealed class DesktopMaintenanceTests : IDisposable
{
    private readonly TemporaryDirectory _data = new("maintenance");
    private readonly IAppPaths _paths = Substitute.For<IAppPaths>();
    private readonly IIconCache _icons = Substitute.For<IIconCache>();

    public DesktopMaintenanceTests()
    {
        _paths.DataDirectory.Returns(_data.FullPath);
        _paths.DatabasePath.Returns(_data.Resolve("index.db"));
        _paths.LogsDirectory.Returns(_data.Resolve("logs"));
        _paths.CacheDirectory.Returns(_data.Resolve("cache"));
        _paths.IconCacheDirectory.Returns(_data.Resolve(Path.Combine("cache", "icons")));
        _paths.ThumbnailCacheDirectory.Returns(_data.Resolve(Path.Combine("cache", "thumbnails")));
    }

    [Fact]
    public async Task Sizes_AndClearCache_TouchOnlyCache()
    {
        // Arrange
        _data.CreateFile("index.db", new byte[1000]);
        _data.CreateFile("index.db-wal", new byte[200]);
        _data.CreateFile(Path.Combine("cache", "icons", "v1", "a.png"), new byte[300]);
        _data.CreateFile(Path.Combine("cache", "thumbnails", "b.png"), new byte[400]);
        var maintenance = new DesktopMaintenance(_paths, Substitute.For<IDiskAppApi>(), new LoggerConfiguration().CreateLogger(), _icons);

        // Act
        var database = maintenance.DatabaseSize;
        var cacheBefore = maintenance.CacheSize;
        await maintenance.ClearCacheAsync();

        // Assert
        database.ShouldBe(1200);
        cacheBefore.ShouldBe(700);
        maintenance.CacheSize.ShouldBe(0);
        File.Exists(_data.Resolve("index.db")).ShouldBeTrue("база не трогается");
        _icons.Received(1).Clear();
    }

    [Fact]
    public void ThirdParty_ListsShippedPackagesWithoutTestTools()
    {
        // Arrange
        var maintenance = new DesktopMaintenance(_paths, Substitute.For<IDiskAppApi>(), new LoggerConfiguration().CreateLogger());

        // Act
        var components = maintenance.ThirdParty;

        // Assert
        components.ShouldContain(component => component.Name == "Avalonia");
        components.ShouldContain(component => component.Name == "FluentAvaloniaUI" && component.Version == "3.1.0");
        components.ShouldNotContain(component => component.Name == "xunit.v3" || component.Name == "Shouldly");
        components.First(component => component.Name == "Dapper").Link.ToString().ShouldStartWith("https://www.nuget.org/packages/Dapper/");
    }

    [Fact]
    public async Task RebuildIndex_CallsServer()
    {
        // Arrange
        var api = Substitute.For<IDiskAppApi>();
        var maintenance = new DesktopMaintenance(_paths, api, new LoggerConfiguration().CreateLogger());

        // Act
        await maintenance.RebuildIndexAsync();

        // Assert
        await api.Received(1).RebuildIndexAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadLogTail_LargeOpenLog_ReturnsLastWholeLines()
    {
        // Arrange
        var lines = Enumerable.Range(1, 40_000).Select(static number => $"10:00:00.000 [INF] Строка {number}").ToList();
        _data.CreateFile(Path.Combine("logs", "diskapp-20261010.log"), "старый");
        File.SetLastWriteTimeUtc(_data.Resolve(Path.Combine("logs", "diskapp-20261010.log")), DateTime.UtcNow.AddDays(-1));
        var current = _data.CreateFile(Path.Combine("logs", "diskapp-20261011.log"), string.Join("\r\n", lines) + "\r\n");
        await using var writer = new FileStream(current, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var maintenance = new DesktopMaintenance(_paths, Substitute.For<IDiskAppApi>(), new LoggerConfiguration().CreateLogger());

        // Act
        var tail = await maintenance.ReadLogTailAsync(3);

        // Assert
        tail.ShouldBe(["10:00:00.000 [INF] Строка 39998", "10:00:00.000 [INF] Строка 39999", "10:00:00.000 [INF] Строка 40000"]);
    }

    [Fact]
    public async Task ReadLogTail_NoLogs_Empty()
    {
        // Arrange
        var maintenance = new DesktopMaintenance(_paths, Substitute.For<IDiskAppApi>(), new LoggerConfiguration().CreateLogger());

        // Act
        var tail = await maintenance.ReadLogTailAsync(10);

        // Assert
        tail.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResetSettings_KeepsCopyAndSavesDefaults()
    {
        // Arrange
        var settingsPath = _data.CreateFile("settings.json", """{"appearance":{"theme":"Dark"}}""");
        _paths.SettingsPath.Returns(settingsPath);
        var store = Substitute.For<ISettingsStore<DiskApp.Core.Settings.AppSettings>>();
        var maintenance = new DesktopMaintenance(_paths, Substitute.For<IDiskAppApi>(), new LoggerConfiguration().CreateLogger(), settings: store);

        // Act
        var backup = await maintenance.ResetSettingsAsync();

        // Assert
        backup.ShouldNotBeNull();
        (await File.ReadAllTextAsync(backup, TestContext.Current.CancellationToken)).ShouldContain("Dark");
        await store.Received(1).SaveAsync(Arg.Is<DiskApp.Core.Settings.AppSettings>(settings => settings.Appearance.Theme == DiskApp.Core.Settings.ThemeMode.System), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateDiagnosticArchive_WritesZipIntoDataFolder()
    {
        // Arrange
        _data.CreateFile(Path.Combine("logs", "diskapp.log"), "10:00:00.000 [INF] Привет");
        _paths.SettingsPath.Returns(_data.Resolve("settings.json"));
        var maintenance = new DesktopMaintenance(_paths, Substitute.For<IDiskAppApi>(), new LoggerConfiguration().CreateLogger());

        // Act
        var path = await maintenance.CreateDiagnosticArchiveAsync();

        // Assert
        path.ShouldStartWith(_data.Resolve("diagnostics"));
        using var zip = System.IO.Compression.ZipFile.OpenRead(path);
        zip.Entries.Select(static entry => entry.FullName).ShouldBe(["environment.txt", "logs/diskapp.log"]);
        using var reader = new StreamReader(zip.GetEntry("environment.txt")!.Open());
        reader.ReadToEnd().ShouldContain("Версия DiskApp: " + AppVersion.Current);
    }

    public void Dispose() => _data.Dispose();
}

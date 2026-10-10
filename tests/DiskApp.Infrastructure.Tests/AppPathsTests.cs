namespace DiskApp.Infrastructure.Tests;

/// <summary>
/// Папки приложения: обычный режим в <c>%LocalAppData%</c>, портативный по маркеру или аргументу, создание подпапок.
/// </summary>
public sealed class AppPathsTests
{
    [Fact]
    public void Resolve_NoMarkerNoArgument_UsesLocalAppDataAndCreatesFolders()
    {
        // Arrange
        using var folder = new TemporaryDirectory();
        var exeDirectory = folder.CreateDirectory("app");
        var localAppData = folder.CreateDirectory("local");

        // Act
        var paths = AppPaths.Resolve([], exeDirectory, localAppData);

        // Assert
        paths.IsPortable.ShouldBeFalse();
        paths.DataDirectory.ShouldBe(Path.Combine(localAppData, "DiskApp"));
        paths.DatabasePath.ShouldBe(Path.Combine(paths.DataDirectory, "index.db"));
        paths.SettingsPath.ShouldBe(Path.Combine(paths.DataDirectory, "settings.json"));
        paths.LogsDirectory.ShouldBe(Path.Combine(paths.DataDirectory, "logs"));
        paths.IconCacheDirectory.ShouldBe(Path.Combine(paths.DataDirectory, "cache", "icons"));
        paths.ThumbnailCacheDirectory.ShouldBe(Path.Combine(paths.DataDirectory, "cache", "thumbs"));
        paths.BackupsDirectory.ShouldBe(Path.Combine(paths.DataDirectory, "backups"));
        Directory.Exists(paths.LogsDirectory).ShouldBeTrue();
        Directory.Exists(paths.IconCacheDirectory).ShouldBeTrue();
        Directory.Exists(paths.ThumbnailCacheDirectory).ShouldBeTrue();
        Directory.Exists(paths.BackupsDirectory).ShouldBeTrue();
    }

    [Fact]
    public void Resolve_PortableMarkerFile_UsesDataFolderNextToExe()
    {
        // Arrange
        using var folder = new TemporaryDirectory();
        var exeDirectory = folder.CreateDirectory("app");
        folder.CreateFile(Path.Combine("app", AppPaths.PortableMarkerFileName));

        // Act
        var paths = AppPaths.Resolve([], exeDirectory, folder.CreateDirectory("local"));

        // Assert
        paths.IsPortable.ShouldBeTrue();
        paths.DataDirectory.ShouldBe(Path.Combine(exeDirectory, "data"));
        Directory.Exists(paths.LogsDirectory).ShouldBeTrue();
    }

    [Theory]
    [InlineData("--portable")]
    [InlineData("--PORTABLE")]
    public void Resolve_PortableArgument_UsesDataFolderNextToExe(string argument)
    {
        // Arrange
        using var folder = new TemporaryDirectory();
        var exeDirectory = folder.CreateDirectory("app");

        // Act
        var paths = AppPaths.Resolve(["--minimized", argument], exeDirectory, folder.CreateDirectory("local"));

        // Assert
        paths.IsPortable.ShouldBeTrue();
        paths.DataDirectory.ShouldBe(Path.Combine(exeDirectory, "data"));
    }

    [Fact]
    public void ForDataDirectory_RelativePath_IsMadeAbsolute()
    {
        // Arrange
        using var folder = new TemporaryDirectory();

        // Act
        var paths = AppPaths.ForDataDirectory(folder.Resolve("data"), isPortable: true);

        // Assert
        Path.IsPathRooted(paths.DataDirectory).ShouldBeTrue();
        paths.IsPortable.ShouldBeTrue();
        Directory.Exists(paths.DataDirectory).ShouldBeTrue();
    }
}

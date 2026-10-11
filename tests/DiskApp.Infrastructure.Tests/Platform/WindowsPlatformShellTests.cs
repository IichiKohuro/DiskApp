using System.Runtime.Versioning;
using System.Text;

using DiskApp.Infrastructure.Platform.Windows;

namespace DiskApp.Infrastructure.Tests.Platform;

/// <summary>
/// Действия оболочки Windows (PLAN.md, этап 7.3): форматы данных для оболочки и буфера обмена, проверки до вызова
/// оболочки. Настоящая корзина и буфер обмена трогаются только при <c>DISKAPP_SHELL_TESTS=1</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPlatformShellTests : IDisposable
{
    private const string LiveTestsVariable = "DISKAPP_SHELL_TESTS";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "DiskApp.ShellTests", Guid.NewGuid().ToString("N"));

    public WindowsPlatformShellTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void DoubleNullList_SeveralPaths_EndsEachWithNullAndListWithAnother()
    {
        // Act
        var list = ShellNative.DoubleNullList([@"C:\a.txt", @"D:\папка\b.txt"]);

        // Assert
        list.ShouldBe(@"C:\a.txt" + "\0" + @"D:\папка\b.txt" + "\0\0");
    }

    [Fact]
    public void DropFiles_Paths_WritesWideHeaderAndUtf16List()
    {
        // Act
        var data = ShellNative.DropFiles([@"C:\a.txt", @"C:\б.txt"]);

        // Assert
        BitConverter.ToInt32(data, 0).ShouldBe(20);
        BitConverter.ToInt32(data, 16).ShouldBe(1);
        Encoding.Unicode.GetString(data, 20, data.Length - 20).ShouldBe(@"C:\a.txt" + "\0" + @"C:\б.txt" + "\0\0");
    }

    [Fact]
    public void HasRecycleBin_NetworkPath_IsFalse()
    {
        // Act & Assert
        WindowsPlatformShell.HasRecycleBin(@"\\server\share\file.txt").ShouldBeFalse();
    }

    [Fact]
    public void HasRecycleBin_LocalFixedDrive_IsTrue()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Корзина есть только в Windows.");
        }

        var root = Path.GetPathRoot(_directory)!;
        if (new DriveInfo(root).DriveType != DriveType.Fixed)
        {
            Assert.Skip("Временная папка не на локальном диске.");
        }

        // Act & Assert
        WindowsPlatformShell.HasRecycleBin(Path.Combine(_directory, "file.txt")).ShouldBeTrue();
    }

    [Fact]
    public async Task Actions_MissingFile_ReturnNotFoundWithoutCallingShell()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Оболочка есть только в Windows.");
        }

        // Arrange
        using var shell = new WindowsPlatformShell();
        var missing = Path.Combine(_directory, "missing.txt");
        var token = TestContext.Current.CancellationToken;

        // Act
        var results = new[]
        {
            await shell.OpenAsync(missing, token),
            await shell.OpenWithAsync(missing, token),
            await shell.RevealInExplorerAsync(missing, token),
            await shell.ShowPropertiesAsync(missing, token),
            await shell.RunAsAdminAsync(missing, token),
            await shell.MoveToRecycleBinAsync([missing], token),
            await shell.CopyFilesToClipboardAsync([missing], token),
        };

        // Assert
        results.ShouldAllBe(result => result.IsFailure && result.Error!.Code == "shell.not_found");
    }

    [Fact]
    public async Task MoveToRecycleBin_EmptyList_SucceedsWithoutCallingShell()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Оболочка есть только в Windows.");
        }

        // Arrange
        using var shell = new WindowsPlatformShell();

        // Act
        var result = await shell.MoveToRecycleBinAsync([], TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task MoveToRecycleBin_LiveTemporaryFile_DisappearsFromDisk()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable(LiveTestsVariable) != "1")
        {
            Assert.Skip($"Настоящая корзина — только при {LiveTestsVariable}=1.");
        }

        // Arrange
        using var shell = new WindowsPlatformShell();
        var file = Path.Combine(_directory, "DiskApp shell test.txt");
        await File.WriteAllTextAsync(file, "временный файл теста DiskApp", TestContext.Current.CancellationToken);

        // Act
        var result = await shell.MoveToRecycleBinAsync([file], TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        File.Exists(file).ShouldBeFalse();
    }

    [Fact]
    public async Task CopyPathsToClipboard_Live_Succeeds()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable(LiveTestsVariable) != "1")
        {
            Assert.Skip($"Настоящий буфер обмена — только при {LiveTestsVariable}=1.");
        }

        // Arrange
        using var shell = new WindowsPlatformShell();

        // Act
        var result = await shell.CopyPathsToClipboardAsync([@"C:\a.txt", @"C:\b.txt"], TestContext.Current.CancellationToken);

        // Assert
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Временная папка теста; удалится вместе с остальным %TEMP%.
        }
    }
}

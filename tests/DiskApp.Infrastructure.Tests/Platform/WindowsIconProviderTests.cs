using System.Runtime.Versioning;

using DiskApp.Core.Abstractions;
using DiskApp.Infrastructure.Imaging;
using DiskApp.Infrastructure.Platform.Windows;

namespace DiskApp.Infrastructure.Tests.Platform;

/// <summary>
/// Иконки и миниатюры из оболочки Windows (PLAN.md, этап 7.4): обработка пикселей и живые вызовы оболочки —
/// только чтение, ничего на диске пользователя не меняется.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsIconProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "DiskApp.IconTests", Guid.NewGuid().ToString("N"));

    public WindowsIconProviderTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Unpremultiply_PremultipliedPixel_RestoresStraightColor()
    {
        // Arrange: красный с альфой 128, премультиплицированный.
        byte[] pixels = [0, 0, 128, 128];

        // Act
        WindowsIconProvider.Unpremultiply(pixels);

        // Assert
        pixels.ShouldBe(new byte[] { 0, 0, 255, 128 });
    }

    [Fact]
    public void Unpremultiply_NoAlpha_MakesOpaque()
    {
        // Arrange
        byte[] pixels = [10, 20, 30, 0, 40, 50, 60, 0];

        // Act
        WindowsIconProvider.Unpremultiply(pixels);

        // Assert
        pixels.ShouldBe(new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 });
    }

    [Fact]
    public void Unpremultiply_StraightAlpha_KeepsPixels()
    {
        // Arrange: цвет ярче альфы — значит, альфа уже прямая.
        byte[] pixels = [200, 200, 200, 100];

        // Act
        WindowsIconProvider.Unpremultiply(pixels);

        // Assert
        pixels.ShouldBe(new byte[] { 200, 200, 200, 100 });
    }

    [Fact]
    public void ApplyMask_ColorWithoutAlpha_TakesTransparencyFromMask()
    {
        // Arrange
        byte[] color = [1, 2, 3, 0, 4, 5, 6, 0];
        byte[] mask = [255, 255, 255, 0, 0, 0, 0, 0];

        // Act
        WindowsIconProvider.ApplyMask(color, mask);

        // Assert
        color[3].ShouldBe((byte)0, "белый пиксель маски — прозрачный");
        color[7].ShouldBe((byte)255);
    }

    [Fact]
    public void CropToContent_SmallIconInCorner_CropsToStandardSize()
    {
        // Arrange: 256×256, непрозрачен квадрат 40×40 в левом верхнем углу.
        var pixels = new byte[256 * 256 * 4];
        for (var y = 0; y < 40; y++)
        {
            for (var x = 0; x < 40; x++)
            {
                pixels[(((y * 256) + x) * 4) + 3] = 255;
            }
        }

        // Act
        var (cropped, size) = WindowsIconProvider.CropToContent(pixels, 256);

        // Assert
        size.ShouldBe(40);
        cropped.Length.ShouldBe(40 * 40 * 4);
        cropped[3].ShouldBe((byte)255);
    }

    [Fact]
    public void CropToContent_FullSizeIcon_KeepsIt()
    {
        // Arrange
        var pixels = new byte[256 * 256 * 4];
        pixels[(((200 * 256) + 200) * 4) + 3] = 255;

        // Act
        var (cropped, size) = WindowsIconProvider.CropToContent(pixels, 256);

        // Assert
        size.ShouldBe(256);
        cropped.ShouldBeSameAs(pixels);
    }

    [Theory]
    [InlineData("txt", 16)]
    [InlineData("txt", 32)]
    [InlineData(".PDF", 48)]
    [InlineData("zip", 256)]
    public async Task GetIconForExtension_Live_ReturnsPngAndCachesOnDisk(string extension, int size)
    {
        SkipUnlessWindows();

        // Arrange
        var paths = AppPaths.ForDataDirectory(_directory, isPortable: true);
        using var provider = new WindowsIconProvider(paths);

        // Act
        var icon = await provider.GetIconForExtensionAsync(extension, size, TestContext.Current.CancellationToken);

        // Assert
        SkipOnCiIfShellIsMissing(icon);
        icon.ShouldNotBeNull();
        IsPng(icon).ShouldBeTrue();
        icon.Width.ShouldBeGreaterThanOrEqualTo(Math.Min(size, 16));
        icon.Width.ShouldBeLessThanOrEqualTo(256);
        Directory.EnumerateFiles(Path.Combine(paths.IconCacheDirectory, "v1"), "*.png").ShouldNotBeEmpty();
    }

    [Fact]
    public async Task GetIconForFile_FolderAndMissingFile_ReturnFolderIconOrNull()
    {
        SkipUnlessWindows();

        // Arrange
        using var provider = new WindowsIconProvider();
        var token = TestContext.Current.CancellationToken;

        // Act
        var folder = await provider.GetIconForFileAsync(_directory, 32, token);
        var missing = await provider.GetIconForFileAsync(Path.Combine(_directory, "missing.exe"), 32, token);

        // Assert
        SkipOnCiIfShellIsMissing(folder);
        folder.ShouldNotBeNull();
        missing.ShouldBeNull();
    }

    [Fact]
    public async Task GetIconForFile_OwnFile_ReturnsIconAndReusesCache()
    {
        SkipUnlessWindows();

        // Arrange
        using var provider = new WindowsIconProvider();
        var file = Path.Combine(_directory, "notes.txt");
        await File.WriteAllTextAsync(file, "заметка", TestContext.Current.CancellationToken);

        // Act
        var first = await provider.GetIconForFileAsync(file, 32, TestContext.Current.CancellationToken);
        var second = await provider.GetIconForFileAsync(file, 32, TestContext.Current.CancellationToken);

        // Assert
        SkipOnCiIfShellIsMissing(first);
        first.ShouldNotBeNull();
        second.ShouldBeSameAs(first, "второй раз — из кэша в памяти");
    }

    [Fact]
    public async Task GetThumbnail_PngFile_ReturnsScaledPreview()
    {
        SkipUnlessWindows();

        // Arrange
        using var provider = new WindowsIconProvider();
        var file = Path.Combine(_directory, "red.png");
        var red = Enumerable.Repeat(new byte[] { 0, 0, 255, 255 }, 64 * 64).SelectMany(static pixel => pixel).ToArray();
        await File.WriteAllBytesAsync(file, PngEncoder.EncodeBgra(red, 64, 64), TestContext.Current.CancellationToken);

        // Act
        var thumbnail = await provider.GetThumbnailAsync(file, 32, TestContext.Current.CancellationToken);
        var missing = await provider.GetThumbnailAsync(Path.Combine(_directory, "missing.png"), 32, TestContext.Current.CancellationToken);

        // Assert
        SkipOnCiIfShellIsMissing(thumbnail);
        thumbnail.ShouldNotBeNull();
        IsPng(thumbnail).ShouldBeTrue();
        thumbnail.Width.ShouldBeLessThanOrEqualTo(64);
        missing.ShouldBeNull();
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

    private static bool IsPng(RasterImage image) => image.Png.Length > 8 && image.Png.Span[1] == (byte)'P' && image.Png.Span[2] == (byte)'N';

    private static void SkipUnlessWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Оболочка Windows есть только в Windows.");
        }
    }

    // На агенте CI может не быть обработчиков оболочки (иконок типов, миниатюр): там отсутствие картинки — не ошибка.
    private static void SkipOnCiIfShellIsMissing(RasterImage? image)
    {
        if (image is null && Environment.GetEnvironmentVariable("CI") == "true")
        {
            Assert.Skip("На агенте CI оболочка не отдала изображение.");
        }
    }
}

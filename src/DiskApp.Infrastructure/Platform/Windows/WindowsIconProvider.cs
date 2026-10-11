using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.Versioning;

using DiskApp.Core.Abstractions;
using DiskApp.Infrastructure.Imaging;

namespace DiskApp.Infrastructure.Platform.Windows;

/// <summary>
/// Иконки и миниатюры из оболочки Windows (PLAN.md, этап 7.4). Иконка вида файла — из системного списка изображений
/// по расширению (кэш в памяти и PNG на диске в <see cref="IAppPaths.IconCacheDirectory"/>, обновляется раз в неделю);
/// собственная иконка программы или ярлыка и миниатюра — <c>IShellItemImageFactory</c> (кэш в памяти по пути и времени
/// изменения). Иконки и миниатюры извлекаются на разных STA-потоках, чтобы медленная миниатюра видео не задерживала список.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsIconProvider : IIconProvider, IDisposable
{
    private static readonly TimeSpan DiskCacheLifetime = TimeSpan.FromDays(7);
    private static readonly int[] CropSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128];

    private readonly StaThread _iconThread = new("DiskApp icons");
    private readonly StaThread _thumbnailThread = new("DiskApp thumbnails");
    private readonly LruCache<string, RasterImage?> _icons = new(512);
    private readonly LruCache<string, RasterImage?> _thumbnails = new(64);
    private readonly ConcurrentDictionary<string, Task<RasterImage?>> _inFlight = new();
    private readonly string? _diskCache;

    /// <summary>
    /// Создаёт провайдер; без <paramref name="paths"/> иконки кэшируются только в памяти.
    /// </summary>
    public WindowsIconProvider(IAppPaths? paths = null)
    {
        _diskCache = paths is null ? null : Path.Combine(paths.IconCacheDirectory, "v1");
    }

    /// <inheritdoc />
    public Task<RasterImage?> GetIconForExtensionAsync(string extension, int size, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extension);
        var normalized = extension.TrimStart('.').ToLowerInvariant();
        return normalized.Length == 0 || normalized.Any(static c => !char.IsLetterOrDigit(c) && c is not ('_' or '-' or '+'))
            ? GenericAsync("file", "file", IconNative.FileAttributeNormal, size, cancellationToken)
            : GenericAsync("ext-" + normalized, "file." + normalized, IconNative.FileAttributeNormal, size, cancellationToken);
    }

    /// <inheritdoc />
    public Task<RasterImage?> GetIconForFileAsync(string path, int size, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (Directory.Exists(path))
        {
            // Ключ иконки папок у клиента общий («folder»): отдаём общую иконку папки, а не значок конкретной.
            return GenericAsync("folder", "folder", IconNative.FileAttributeDirectory, size, cancellationToken);
        }

        if (!File.Exists(path))
        {
            return Task.FromResult<RasterImage?>(null);
        }

        var key = Key("file", path, size);
        return CachedAsync(_icons, key, async () =>
            await _iconThread.RunAsync(() => FromImageFactory(path, size, IconNative.SiigbfIconOnly)).ConfigureAwait(false)
            ?? await GetIconForExtensionAsync(Path.GetExtension(path), size, CancellationToken.None).ConfigureAwait(false), cancellationToken);
    }

    /// <inheritdoc />
    public Task<RasterImage?> GetThumbnailAsync(string path, int size, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
        {
            return Task.FromResult<RasterImage?>(null);
        }

        var key = Key("thumb", path, size);
        return CachedAsync(_thumbnails, key, () => _thumbnailThread.RunAsync(() => FromImageFactory(path, size, IconNative.SiigbfThumbnailOnly)), cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _iconThread.Dispose();
        _thumbnailThread.Dispose();
    }

    /// <summary>
    /// Пиксели иконки с полупрозрачностью из маски, если у цветной части нет альфа-канала.
    /// </summary>
    internal static void ApplyMask(Span<byte> color, ReadOnlySpan<byte> mask)
    {
        for (var i = 3; i < color.Length; i += 4)
        {
            if (color[i] != 0)
            {
                return;
            }
        }

        for (var i = 0; i < color.Length; i += 4)
        {
            // Белый пиксель маски — прозрачный.
            color[i + 3] = mask[i] == 0 && mask[i + 1] == 0 && mask[i + 2] == 0 ? (byte)255 : (byte)0;
        }
    }

    /// <summary>
    /// Переводит премультиплицированную альфу в прямую, если пиксели похожи на премультиплицированные
    /// (ни один канал не ярче альфы); пиксели без альфы делает непрозрачными.
    /// </summary>
    internal static void Unpremultiply(Span<byte> bgra)
    {
        var anyAlpha = false;
        var premultiplied = true;
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var alpha = bgra[i + 3];
            anyAlpha |= alpha != 0;
            if (bgra[i] > alpha || bgra[i + 1] > alpha || bgra[i + 2] > alpha)
            {
                premultiplied = false;
            }
        }

        if (!anyAlpha)
        {
            for (var i = 3; i < bgra.Length; i += 4)
            {
                bgra[i] = 255;
            }

            return;
        }

        if (!premultiplied)
        {
            return;
        }

        for (var i = 0; i < bgra.Length; i += 4)
        {
            var alpha = bgra[i + 3];
            if (alpha is > 0 and < 255)
            {
                bgra[i] = (byte)((bgra[i] * 255) / alpha);
                bgra[i + 1] = (byte)((bgra[i + 1] * 255) / alpha);
                bgra[i + 2] = (byte)((bgra[i + 2] * 255) / alpha);
            }
        }
    }

    /// <summary>
    /// Обрезает квадратную иконку, нарисованную в левом верхнем углу большого холста (так Windows отдаёт иконки
    /// без кадра 256×256 из списка «jumbo»), до ближайшего стандартного размера.
    /// </summary>
    internal static (byte[] Pixels, int Size) CropToContent(byte[] bgra, int size)
    {
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (bgra[(((y * size) + x) * 4) + 3] != 0)
                {
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        var content = Math.Max(maxX, maxY) + 1;
        if (content <= 0 || content > size / 2)
        {
            return (bgra, size);
        }

        var target = CropSizes.First(candidate => candidate >= content);
        var cropped = new byte[target * target * 4];
        for (var y = 0; y < target; y++)
        {
            bgra.AsSpan(y * size * 4, target * 4).CopyTo(cropped.AsSpan(y * target * 4));
        }

        return (cropped, target);
    }

    private static int ImageListFor(int size) => size switch
    {
        <= 16 => IconNative.ShilSmall,
        <= 32 => IconNative.ShilLarge,
        <= 48 => IconNative.ShilExtraLarge,
        _ => IconNative.ShilJumbo,
    };

    private static string Key(string kind, string path, int size)
    {
        var modified = File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture);
        return string.Join('|', kind, path, modified, size.ToString(CultureInfo.InvariantCulture));
    }

    private static RasterImage? FromImageList(string name, uint attributes, int size)
    {
        var info = default(IconNative.ShFileInfo);
        unsafe
        {
            if (IconNative.ShGetFileInfo(name, attributes, ref info, (uint)sizeof(IconNative.ShFileInfo), IconNative.ShgfiSysIconIndex | IconNative.ShgfiUseFileAttributes) == 0)
            {
                return null;
            }
        }

        var list = ImageListFor(size);
        if (IconNative.ShGetImageList(list, IconNative.ImageListId, out var imageList) < 0 || imageList == 0)
        {
            return null;
        }

        try
        {
            if (IconNative.ImageListGetIcon(imageList, info.IconIndex, IconNative.IldTransparent, out var icon) < 0 || icon == 0)
            {
                return null;
            }

            try
            {
                return FromIcon(icon, cropJumbo: list == IconNative.ShilJumbo);
            }
            finally
            {
                IconNative.DestroyIcon(icon);
            }
        }
        finally
        {
            IconNative.Release(imageList);
        }
    }

    private static RasterImage? FromIcon(nint icon, bool cropJumbo)
    {
        if (!IconNative.GetIconInfo(icon, out var parts))
        {
            return null;
        }

        try
        {
            if (parts.Color == 0 || IconNative.ReadPixels(parts.Color, out var width, out var height) is not { } pixels)
            {
                return null;
            }

            if (parts.Mask != 0 && IconNative.ReadPixels(parts.Mask, out var maskWidth, out var maskHeight) is { } mask
                && maskWidth == width && maskHeight == height)
            {
                ApplyMask(pixels, mask);
            }

            if (cropJumbo && width == height)
            {
                (pixels, width) = CropToContent(pixels, width);
                height = width;
            }

            return new RasterImage(PngEncoder.EncodeBgra(pixels, width, height), width, height);
        }
        finally
        {
            IconNative.DeleteObject(parts.Color);
            IconNative.DeleteObject(parts.Mask);
        }
    }

    private static RasterImage? FromImageFactory(string path, int size, int flags)
    {
        if (IconNative.ShCreateItemFromParsingName(path, 0, IconNative.ShellItemImageFactoryId, out var factory) < 0 || factory == 0)
        {
            return null;
        }

        try
        {
            if (IconNative.ImageFactoryGetImage(factory, new IconNative.NativeSize(size, size), flags, out var bitmap) < 0 || bitmap == 0)
            {
                return null;
            }

            try
            {
                if (IconNative.ReadPixels(bitmap, out var width, out var height) is not { } pixels)
                {
                    return null;
                }

                Unpremultiply(pixels);
                return new RasterImage(PngEncoder.EncodeBgra(pixels, width, height), width, height);
            }
            finally
            {
                IconNative.DeleteObject(bitmap);
            }
        }
        finally
        {
            IconNative.Release(factory);
        }
    }

    private Task<RasterImage?> GenericAsync(string cacheName, string shellName, uint attributes, int size, CancellationToken cancellationToken)
    {
        var key = cacheName + "|" + size.ToString(CultureInfo.InvariantCulture);
        return CachedAsync(_icons, key, async () =>
        {
            var file = _diskCache is null ? null : Path.Combine(_diskCache, $"{cacheName}-{size.ToString(CultureInfo.InvariantCulture)}.png");
            if (file is not null && ReadDiskCache(file) is { } cached)
            {
                return cached;
            }

            var image = await _iconThread.RunAsync(() => FromImageList(shellName, attributes, size)).ConfigureAwait(false);
            if (image is not null && file is not null)
            {
                WriteDiskCache(file, image);
            }

            return image;
        }, cancellationToken);
    }

    private Task<RasterImage?> CachedAsync(LruCache<string, RasterImage?> cache, string key, Func<Task<RasterImage?>> load, CancellationToken cancellationToken)
    {
        if (cache.TryGet(key, out var cached))
        {
            return Task.FromResult(cached);
        }

        // Одна загрузка на ключ, даже если строку с этой иконкой показали несколько раз подряд.
        var task = _inFlight.GetOrAdd(key, _ => LoadAsync());
        return task.WaitAsync(cancellationToken);

        async Task<RasterImage?> LoadAsync()
        {
            try
            {
                var image = await load().ConfigureAwait(false);
                cache.Set(key, image);
                return image;
            }
            finally
            {
                _inFlight.TryRemove(key, out _);
            }
        }
    }

    private static RasterImage? ReadDiskCache(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || DateTime.UtcNow - info.LastWriteTimeUtc > DiskCacheLifetime)
            {
                return null;
            }

            var png = File.ReadAllBytes(file);
            if (png.Length < 24)
            {
                return null;
            }

            var width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16));
            var height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20));
            return new RasterImage(png, width, height);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteDiskCache(string file, RasterImage image)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, image.Png.ToArray());
            File.Move(temporary, file, overwrite: true);
        }
        catch (IOException)
        {
            // Кэш на диске — ускорение, а не обязанность: без него иконка возьмётся из оболочки в следующий раз.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

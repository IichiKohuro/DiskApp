namespace DiskApp.Core.Abstractions;

/// <summary>
/// Иконки и миниатюры для записей индекса. Реализация кэширует растры на диске (<see cref="IAppPaths.IconCacheDirectory"/>).
/// </summary>
public interface IIconProvider
{
    /// <summary>Иконка для вида файла по расширению (общая для всех файлов с этим расширением).</summary>
    Task<RasterImage?> GetIconForExtensionAsync(string extension, int size, CancellationToken cancellationToken = default);

    /// <summary>Иконка конкретного файла: исполняемые файлы и ярлыки несут собственную.</summary>
    Task<RasterImage?> GetIconForFileAsync(string path, int size, CancellationToken cancellationToken = default);

    /// <summary>Миниатюра изображения, видео или документа; <c>null</c>, если её нет.</summary>
    Task<RasterImage?> GetThumbnailAsync(string path, int size, CancellationToken cancellationToken = default);
}

/// <summary>
/// Растровое изображение в PNG: не зависит от UI-фреймворка, передаётся по API как есть.
/// </summary>
/// <param name="Png">Байты PNG.</param>
/// <param name="Width">Ширина в пикселях.</param>
/// <param name="Height">Высота в пикселях.</param>
public sealed record RasterImage(ReadOnlyMemory<byte> Png, int Width, int Height);

using Avalonia.Media;

using DiskApp.UI.Models;

namespace DiskApp.UI.Services;

/// <summary>
/// Растровые иконки и миниатюры записей для списков (PLAN.md, этап 7.4). Иконки по ключу сервера общие для файлов
/// одного вида (<c>ext:pdf</c>), у программ и ярлыков — свои; миниатюры — по записи и времени изменения.
/// </summary>
public interface IIconCache
{
    /// <summary>
    /// Готовое изображение без ожидания: строка, показанная повторно, не мигает заглушкой.
    /// </summary>
    /// <returns><c>true</c>, если ответ известен (в том числе «изображения нет»).</returns>
    bool TryGet(FileEntry entry, int size, bool thumbnail, out IImage? image);

    /// <summary>
    /// Иконка или миниатюра; <c>null</c> — показывать заглушку по виду файла.
    /// </summary>
    Task<IImage?> GetAsync(FileEntry entry, int size, bool thumbnail, CancellationToken cancellationToken = default);
}

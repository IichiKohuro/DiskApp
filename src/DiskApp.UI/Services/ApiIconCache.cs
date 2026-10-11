using System.Collections.Concurrent;
using System.Globalization;
using System.Net;

using Avalonia.Media;
using Avalonia.Media.Imaging;

using DiskApp.Client;
using DiskApp.UI.Models;

namespace DiskApp.UI.Services;

/// <summary>
/// <see cref="IIconCache"/> поверх клиента API: PNG с сервера (<c>/icon</c>, <c>/thumbnail</c>) превращается
/// в <see cref="Bitmap"/> и хранится в LRU-кэше. Одна загрузка на ключ; «изображения нет» (404, 501) тоже кэшируется,
/// а сбой связи — нет: строка попробует снова, когда появится на экране в следующий раз.
/// </summary>
public sealed class ApiIconCache(IDiskAppApi api) : IIconCache
{
    private readonly IDiskAppApi _api = api ?? throw new ArgumentNullException(nameof(api));
    private readonly LruCache<string, IImage?> _images = new(768);
    private readonly ConcurrentDictionary<string, Task<IImage?>> _inFlight = new();

    /// <inheritdoc />
    public bool TryGet(FileEntry entry, int size, bool thumbnail, out IImage? image)
    {
        image = null;
        return Key(entry, size, thumbnail) is { } key && _images.TryGet(key, out image);
    }

    /// <inheritdoc />
    public void Clear() => _images.Clear();

    /// <inheritdoc />
    public Task<IImage?> GetAsync(FileEntry entry, int size, bool thumbnail, CancellationToken cancellationToken = default)
    {
        if (Key(entry, size, thumbnail) is not { } key)
        {
            return Task.FromResult<IImage?>(null);
        }

        if (_images.TryGet(key, out var cached))
        {
            return Task.FromResult(cached);
        }

        // Загрузку не отменяет тот, кто перестал ждать: строку могли прокрутить, а иконка нужна соседней.
        return _inFlight.GetOrAdd(key, _ => LoadAsync(key, entry.Id, size, thumbnail)).WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Ключ кэша: у иконки — ключ сервера и размер, у миниатюры — запись, время изменения и размер.
    /// <c>null</c> — у записи нет изображения на сервере (макет, пустой ключ).
    /// </summary>
    public static string? Key(FileEntry entry, int size, bool thumbnail)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var pixels = size.ToString(CultureInfo.InvariantCulture);
        return entry.Id <= 0 ? null
            : thumbnail ? $"thumb:{entry.Id.ToString(CultureInfo.InvariantCulture)}:{entry.Modified.UtcTicks.ToString(CultureInfo.InvariantCulture)}|{pixels}"
            : entry.IconKey.Length == 0 ? null
            : entry.IconKey + "|" + pixels;
    }

    private async Task<IImage?> LoadAsync(string key, long id, int size, bool thumbnail)
    {
        try
        {
            var png = thumbnail
                ? await _api.GetThumbnailAsync(id, size).ConfigureAwait(false)
                : await _api.GetIconAsync(id, size).ConfigureAwait(false);
            using var stream = new MemoryStream(png);
            IImage image = new Bitmap(stream);
            _images.Set(key, image);
            return image;
        }
        catch (DiskAppApiException exception) when (exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented or HttpStatusCode.UnprocessableEntity)
        {
            _images.Set(key, null);
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }
}

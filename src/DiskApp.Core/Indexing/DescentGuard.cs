using System.Collections.Concurrent;

using DiskApp.Core.FileSystem;
using DiskApp.Core.Paths;

namespace DiskApp.Core.Indexing;

/// <summary>
/// Решает, заходить ли внутрь папки при обходе. Junction и символические ссылки по умолчанию не обходятся;
/// при включённом переходе по ссылкам каждая папка регистрируется по идентичности (том и номер файла),
/// и ссылка на уже встреченную папку не обходится — так ссылка на предка не зацикливает обход.
/// Потокобезопасен: его делят потоки одного сканирования.
/// </summary>
internal sealed class DescentGuard(IFileSystemLister lister, PathNormalizer paths, bool followReparsePoints)
{
    private readonly ConcurrentDictionary<string, byte> _visited = new(StringComparer.Ordinal);

    /// <summary>Регистрирует начальную папку обхода.</summary>
    public void RegisterStart(string fullPath)
    {
        if (followReparsePoints && lister.GetDirectoryIdentity(fullPath) is { } identity)
        {
            _visited.TryAdd(identity, 0);
        }
    }

    /// <summary>
    /// Заходить ли внутрь папки <paramref name="item"/>, лежащей в <paramref name="parentFullPath"/>.
    /// </summary>
    public bool ShouldDescend(string parentFullPath, in FileSystemItem item)
    {
        if (!followReparsePoints)
        {
            return !item.IsReparsePoint;
        }

        var identity = lister.GetDirectoryIdentity(paths.Combine(parentFullPath, item.Name));
        if (identity is null)
        {
            return !item.IsReparsePoint;
        }

        // Обычные папки обходятся всегда (дубликат через ссылку конечен), ссылки — только если цель ещё не встречалась.
        var isNew = _visited.TryAdd(identity, 0);
        return isNew || !item.IsReparsePoint;
    }
}

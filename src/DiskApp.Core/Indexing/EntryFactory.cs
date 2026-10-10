using DiskApp.Core.Domain;
using DiskApp.Core.FileSystem;
using DiskApp.Core.Paths;

namespace DiskApp.Core.Indexing;

/// <summary>
/// Перевод сведений из файловой системы в записи индекса и разбор относительных путей — общий для сканера,
/// сверки и наблюдателя, чтобы все три давали одинаковые записи.
/// </summary>
public sealed class EntryFactory(PathNormalizer paths, FileKindMap kinds)
{
    /// <summary>Правила путей.</summary>
    public PathNormalizer Paths { get; } = paths;

    /// <summary>Таблица видов файлов.</summary>
    public FileKindMap Kinds { get; } = kinds;

    /// <summary>
    /// Запись индекса для элемента папки.
    /// </summary>
    /// <param name="rootId">Корень.</param>
    /// <param name="relPath">Путь элемента от корня, уже нормализованный.</param>
    /// <param name="item">Сведения из файловой системы.</param>
    public FileEntry Create(long rootId, string relPath, in FileSystemItem item)
    {
        var extension = item.IsDirectory ? string.Empty : FileEntry.ExtensionOf(item.Name);
        return new FileEntry
        {
            RootId = rootId,
            Name = item.Name,
            Extension = extension,
            IsDirectory = item.IsDirectory,
            Size = item.IsDirectory ? 0 : item.Size,
            CreatedUtc = item.CreatedUtc,
            ModifiedUtc = item.ModifiedUtc,
            Attributes = item.Attributes,
            RelPath = relPath,
            Kind = Kinds.Classify(extension, item.IsDirectory),
        };
    }

    /// <summary>Путь ребёнка: <c>parent\name</c> или просто имя на верхнем уровне.</summary>
    public string Child(string parentRelPath, string name)
        => parentRelPath.Length == 0 ? name : string.Concat(parentRelPath, Paths.Separator.ToString(), name);

    /// <summary>Полный путь по пути от корня.</summary>
    public string FullPath(Root root, string relPath) => relPath.Length == 0 ? root.Path : Paths.Combine(root.Path, relPath);

    /// <summary>Глубина пути: 0 для корня, 1 для верхнего уровня.</summary>
    public int Depth(string relPath) => relPath.Length == 0 ? 0 : relPath.AsSpan().Count(Paths.Separator) + 1;

    /// <summary>Родительская папка пути или пустая строка для верхнего уровня.</summary>
    public string Parent(string relPath)
    {
        var index = relPath.LastIndexOf(Paths.Separator);
        return index < 0 ? string.Empty : relPath[..index];
    }

    /// <summary>Имя — последний сегмент пути.</summary>
    public string Name(string relPath)
    {
        var index = relPath.LastIndexOf(Paths.Separator);
        return index < 0 ? relPath : relPath[(index + 1)..];
    }

    /// <summary>
    /// Изменилась ли запись по сравнению со сведениями с диска — те же поля, по которым репозиторий
    /// пропускает неизменённые строки: тип, имя, размер, время изменения, атрибуты.
    /// </summary>
    public static bool HasChanged(FileEntry indexed, in FileSystemItem item)
        => indexed.IsDirectory != item.IsDirectory
            || !string.Equals(indexed.Name, item.Name, StringComparison.Ordinal)
            || indexed.Size != (item.IsDirectory ? 0 : item.Size)
            || indexed.ModifiedUtc != item.ModifiedUtc
            || indexed.Attributes != item.Attributes;
}

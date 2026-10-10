using DiskApp.Core.Paths;

namespace DiskApp.Core.Domain;

/// <summary>
/// Запись индекса: файл или папка внутри корня. Полный путь не хранится, а собирается из пути корня
/// и <see cref="RelPath"/>, поэтому перенос корня не требует переписывать записи.
/// </summary>
public sealed record FileEntry
{
    /// <summary>Идентификатор в базе; 0 до первого сохранения.</summary>
    public long Id { get; init; }

    /// <summary>Корень, в котором лежит запись.</summary>
    public required long RootId { get; init; }

    /// <summary>Родительская папка; <c>null</c> для записей верхнего уровня корня.</summary>
    public long? ParentId { get; init; }

    /// <summary>Имя файла или папки с расширением.</summary>
    public required string Name { get; init; }

    /// <summary>Расширение без точки в нижнем регистре; пусто у папок, файлов без расширения и имён вида <c>.gitignore</c>.</summary>
    public string Extension { get; init; } = string.Empty;

    /// <summary>Запись — папка.</summary>
    public bool IsDirectory { get; init; }

    /// <summary>Размер в байтах; у папок 0.</summary>
    public long Size { get; init; }

    /// <summary>Время создания (UTC).</summary>
    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Время последнего изменения (UTC).</summary>
    public DateTimeOffset ModifiedUtc { get; init; }

    /// <summary>Атрибуты файловой системы.</summary>
    public FileAttributes Attributes { get; init; }

    /// <summary>Путь относительно корня без ведущего и завершающего разделителя, с разделителем платформы.</summary>
    public required string RelPath { get; init; }

    /// <summary>Вид записи по расширению.</summary>
    public FileKind Kind { get; init; }

    /// <summary>Глубина вложенности: 1 для записей верхнего уровня корня.</summary>
    public int Depth => RelPath.AsSpan().Count('\\') + RelPath.AsSpan().Count('/') + 1;

    /// <summary>Скрытая запись по атрибутам.</summary>
    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0;

    /// <summary>Системная запись по атрибутам.</summary>
    public bool IsSystem => (Attributes & FileAttributes.System) != 0;

    /// <summary>Точка повторной обработки: junction или символическая ссылка.</summary>
    public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>
    /// Создаёт запись по относительному пути: имя, расширение и вид выводятся сами.
    /// </summary>
    /// <param name="rootId">Корень.</param>
    /// <param name="parentId">Родительская папка или <c>null</c>.</param>
    /// <param name="relPath">Путь относительно корня; разделители приводятся к платформенным.</param>
    /// <param name="isDirectory">Запись — папка.</param>
    /// <param name="size">Размер в байтах.</param>
    /// <param name="createdUtc">Время создания.</param>
    /// <param name="modifiedUtc">Время изменения.</param>
    /// <param name="attributes">Атрибуты.</param>
    /// <param name="kinds">Таблица видов; по умолчанию <see cref="FileKindMap.Default"/>.</param>
    /// <param name="normalizer">Правила путей; по умолчанию <see cref="PathNormalizer.Current"/>.</param>
    public static FileEntry Create(
        long rootId,
        long? parentId,
        string relPath,
        bool isDirectory,
        long size,
        DateTimeOffset createdUtc,
        DateTimeOffset modifiedUtc,
        FileAttributes attributes,
        FileKindMap? kinds = null,
        PathNormalizer? normalizer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relPath);

        normalizer ??= PathNormalizer.Current;
        kinds ??= FileKindMap.Default;

        var normalizedRelPath = normalizer.NormalizeRelative(relPath);
        var separatorIndex = normalizedRelPath.LastIndexOf(normalizer.Separator);
        var name = separatorIndex < 0 ? normalizedRelPath : normalizedRelPath[(separatorIndex + 1)..];
        var extension = isDirectory ? string.Empty : ExtensionOf(name);

        return new FileEntry
        {
            RootId = rootId,
            ParentId = parentId,
            Name = name,
            Extension = extension,
            IsDirectory = isDirectory,
            Size = isDirectory ? 0 : size,
            CreatedUtc = createdUtc,
            ModifiedUtc = modifiedUtc,
            Attributes = attributes,
            RelPath = normalizedRelPath,
            Kind = kinds.Classify(extension, isDirectory),
        };
    }

    /// <summary>
    /// Расширение имени без точки в нижнем регистре; пусто для <c>README</c>, <c>.gitignore</c> и <c>name.</c>.
    /// Для <c>archive.tar.gz</c> — <c>gz</c>.
    /// </summary>
    public static string ExtensionOf(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? string.Empty : name[(dot + 1)..].ToLowerInvariant();
    }

    /// <summary>
    /// Полный путь: корень плюс <see cref="RelPath"/>.
    /// </summary>
    /// <param name="rootPath">Нормализованный путь корня.</param>
    /// <param name="normalizer">Правила путей; по умолчанию <see cref="PathNormalizer.Current"/>.</param>
    public string GetFullPath(string rootPath, PathNormalizer? normalizer = null)
        => (normalizer ?? PathNormalizer.Current).Combine(rootPath, RelPath);

    /// <summary>
    /// Полный путь внутри корня.
    /// </summary>
    public string GetFullPath(Root root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return GetFullPath(root.Path);
    }
}

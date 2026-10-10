using DiskApp.Core.Domain;

namespace DiskApp.Infrastructure.Sqlite.Repositories;

/// <summary>
/// Строка таблицы <c>entries</c> как её читает Dapper (колонки <c>snake_case</c> → свойства); даты остаются текстом
/// и разбираются при переводе в <see cref="FileEntry"/>, чтобы горячий путь поиска не зависел от обработчиков типов.
/// </summary>
internal sealed class EntryRow
{
    /// <summary>Колонки в порядке, который ждут запросы репозитория.</summary>
    public const string Columns = "id, root_id, parent_id, name, ext, is_dir, size, created_utc, modified_utc, attributes, rel_path, kind";

    public long Id { get; set; }

    public long RootId { get; set; }

    public long? ParentId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Ext { get; set; } = string.Empty;

    public long IsDir { get; set; }

    public long Size { get; set; }

    public string CreatedUtc { get; set; } = string.Empty;

    public string ModifiedUtc { get; set; } = string.Empty;

    public long Attributes { get; set; }

    public string RelPath { get; set; } = string.Empty;

    public long Kind { get; set; }

    /// <summary>
    /// Запись домена.
    /// </summary>
    public FileEntry ToEntry() => new()
    {
        Id = Id,
        RootId = RootId,
        ParentId = ParentId,
        Name = Name,
        Extension = Ext,
        IsDirectory = IsDir != 0,
        Size = Size,
        CreatedUtc = SqliteTime.Parse(CreatedUtc),
        ModifiedUtc = SqliteTime.Parse(ModifiedUtc),
        Attributes = (FileAttributes)Attributes,
        RelPath = RelPath,
        Kind = (FileKind)Kind,
    };
}

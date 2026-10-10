namespace DiskApp.Core.FileSystem;

/// <summary>
/// Чтение файловой системы для индексации: содержимое одной папки, сведения об одном пути, идентичность папки
/// для защиты от циклов. Логика обхода живёт в ядре (<c>DirectoryScanner</c>), а это — тонкий слой над ОС,
/// который подменяется в тестах. Методы не бросают исключений из-за недоступных путей: ошибка возвращается значением.
/// </summary>
public interface IFileSystemLister
{
    /// <summary>
    /// Содержимое папки без рекурсии и без фильтрации. При ошибке (нет доступа, папка исчезла) — то, что успели
    /// прочитать, и текст ошибки.
    /// </summary>
    DirectoryListing List(string fullPath);

    /// <summary>
    /// Сведения о файле или папке; <c>null</c>, если пути нет. Ошибка доступа отличается от отсутствия:
    /// возвращается <see cref="StatResult.Error"/>, чтобы не удалить из индекса то, что просто временно недоступно.
    /// </summary>
    StatResult Stat(string fullPath);

    /// <summary>Есть ли папка.</summary>
    bool DirectoryExists(string fullPath);

    /// <summary>
    /// Идентичность папки после разрешения ссылок: том и номер файла в Windows, реальный путь в остальных ОС.
    /// <c>null</c>, если определить не удалось. Две папки с одинаковой идентичностью — одна и та же папка.
    /// </summary>
    string? GetDirectoryIdentity(string fullPath);
}

/// <summary>
/// Файл или папка с метаданными для индекса.
/// </summary>
/// <param name="Name">Имя.</param>
/// <param name="IsDirectory">Папка.</param>
/// <param name="Size">Размер файла в байтах; у папок 0.</param>
/// <param name="CreatedUtc">Время создания.</param>
/// <param name="ModifiedUtc">Время изменения.</param>
/// <param name="Attributes">Атрибуты ОС, включая биты облачных заглушек.</param>
public readonly record struct FileSystemItem(
    string Name,
    bool IsDirectory,
    long Size,
    DateTimeOffset CreatedUtc,
    DateTimeOffset ModifiedUtc,
    FileAttributes Attributes)
{
    /// <summary>Атрибут Windows <c>FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS</c>: содержимое скачивается из облака при чтении.</summary>
    public const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    /// <summary>Атрибут Windows <c>FILE_ATTRIBUTE_RECALL_ON_OPEN</c>: заглушка, содержимое не на диске.</summary>
    public const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;

    /// <summary>Точка повторной обработки: junction или символическая ссылка.</summary>
    public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>
    /// Облачная заглушка (OneDrive «по запросу» и подобные): содержимое такого файла нельзя читать при индексации,
    /// иначе он скачается (PLAN.md, этап 2.5).
    /// </summary>
    public bool IsCloudPlaceholder => (Attributes & (RecallOnDataAccess | RecallOnOpen | FileAttributes.Offline)) != 0;
}

/// <summary>
/// Содержимое папки.
/// </summary>
/// <param name="Items">Прочитанные записи.</param>
/// <param name="Error">Ошибка чтения; <c>null</c>, если папка прочитана полностью.</param>
public sealed record DirectoryListing(IReadOnlyList<FileSystemItem> Items, string? Error)
{
    /// <summary>Папка прочитана полностью.</summary>
    public bool Succeeded => Error is null;

    /// <summary>Неудачное чтение без записей.</summary>
    public static DirectoryListing Failed(string error) => new([], error);
}

/// <summary>
/// Итог запроса сведений о пути.
/// </summary>
/// <param name="Item">Сведения; <c>null</c>, если пути нет или произошла ошибка.</param>
/// <param name="Error">Ошибка доступа; <c>null</c>, если путь прочитан или его просто нет.</param>
public readonly record struct StatResult(FileSystemItem? Item, string? Error)
{
    /// <summary>Пути нет.</summary>
    public static StatResult Missing => default;

    /// <summary>Путь существует.</summary>
    public bool Exists => Item is not null;

    /// <summary>Пути точно нет: не найден и без ошибки доступа.</summary>
    public bool IsMissing => Item is null && Error is null;
}

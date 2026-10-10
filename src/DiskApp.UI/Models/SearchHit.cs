using DiskApp.UI.Text;

using FluentIcons.Common;

namespace DiskApp.UI.Models;

/// <summary>
/// Строка результата поиска: запись индекса плюс всё, что нужно для показа — подсветка, номер для <c>Ctrl+1…9</c>,
/// готовые подписи размера, даты и папки.
/// </summary>
public sealed class SearchHit
{
    /// <summary>
    /// Создаёт строку результата.
    /// </summary>
    /// <param name="entry">Запись индекса.</param>
    /// <param name="highlight">Запрос для подсветки совпадений в имени; пусто — без подсветки.</param>
    /// <param name="number">Номер строки 1–9 для быстрого выбора; 0 — без номера.</param>
    /// <param name="now">Момент, относительно которого считается «2 ч назад».</param>
    public SearchHit(FileEntry entry, string highlight, int number, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Entry = entry;
        Highlight = highlight;
        Number = number;
        SizeText = entry.Kind == FileKind.Folder ? string.Empty : Formatting.Size(entry.Size);
        ModifiedText = Formatting.RelativeTime(entry.Modified, now);
        DirectoryText = PathText.Shorten(entry.Directory, 60);
        Symbol = FileKindSymbols.For(entry.Kind);
    }

    /// <summary>Запись индекса.</summary>
    public FileEntry Entry { get; }

    /// <summary>Текст запроса для подсветки совпадений в имени.</summary>
    public string Highlight { get; }

    /// <summary>Номер 1–9 для выбора клавишами; 0, если строка вне первой девятки.</summary>
    public int Number { get; }

    /// <summary>Подпись номера, например «1»; пусто без номера.</summary>
    public string NumberText => Number > 0 ? Number.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>Есть ли номер для быстрого выбора.</summary>
    public bool HasNumber => Number > 0;

    /// <summary>Размер в удобных единицах; пусто для папок.</summary>
    public string SizeText { get; }

    /// <summary>Время изменения относительно текущего момента.</summary>
    public string ModifiedText { get; }

    /// <summary>Папка с сокращённой серединой, чтобы влезала в строку.</summary>
    public string DirectoryText { get; }

    /// <summary>Иконка по виду файла.</summary>
    public Symbol Symbol { get; }

    /// <summary>Файл в избранном.</summary>
    public bool IsFavorite => Entry.IsFavorite;

    /// <summary>У файла есть теги.</summary>
    public bool HasTags => Entry.Tags.Count > 0;

    /// <summary>Теги через точку с запятой для подписи.</summary>
    public string TagsText => string.Join(" · ", Entry.Tags);
}

/// <summary>
/// Соответствие видов файлов иконкам Fluent System Icons.
/// </summary>
public static class FileKindSymbols
{
    /// <summary>
    /// Иконка для вида файла.
    /// </summary>
    public static Symbol For(FileKind kind) => kind switch
    {
        FileKind.Folder => Symbol.Folder,
        FileKind.Document => Symbol.DocumentText,
        FileKind.Spreadsheet => Symbol.DocumentTable,
        FileKind.Presentation => Symbol.DocumentOnePage,
        FileKind.Pdf => Symbol.DocumentPdf,
        FileKind.Image => Symbol.Image,
        FileKind.Video => Symbol.Video,
        FileKind.Audio => Symbol.MusicNote2,
        FileKind.Archive => Symbol.FolderZip,
        FileKind.Installer => Symbol.AppGeneric,
        FileKind.Code => Symbol.Code,
        FileKind.Text => Symbol.DocumentBulletList,
        _ => Symbol.Document,
    };
}

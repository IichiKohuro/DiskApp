using System.Globalization;

using DiskApp.UI.Text;

namespace DiskApp.UI.Models;

/// <summary>
/// Сведения о выбранном файле для панели справа от результатов (PLAN.md, этап 8.1): готовые подписи.
/// </summary>
public sealed class FileDetails
{
    /// <summary>
    /// Собирает сведения о записи.
    /// </summary>
    public FileDetails(FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Entry = entry;
        KindText = KindName(entry.Kind) + (entry.Extension.Length > 0 && entry.Kind != FileKind.Folder ? $" · .{entry.Extension}" : string.Empty);
        SizeText = entry.Kind == FileKind.Folder ? "—" : $"{Formatting.Size(entry.Size)} ({entry.Size.ToString("N0", CultureInfo.CurrentCulture)} байт)";
        ModifiedText = entry.Modified == default ? "—" : entry.Modified.ToLocalTime().ToString("d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture);
        Availability = entry.IsOffline ? "Диск недоступен"
            : entry.IsCloudOnly ? "Только в облаке: откроется после загрузки"
            : string.Empty;
    }

    /// <summary>Запись.</summary>
    public FileEntry Entry { get; }

    /// <summary>Вид файла словами: «Таблица · .xlsx».</summary>
    public string KindText { get; }

    /// <summary>Размер в удобных единицах и в байтах.</summary>
    public string SizeText { get; }

    /// <summary>Дата и время изменения.</summary>
    public string ModifiedText { get; }

    /// <summary>Пояснение о доступности; пусто, если файл на месте.</summary>
    public string Availability { get; }

    /// <summary>Есть ли пояснение о доступности.</summary>
    public bool HasAvailability => Availability.Length > 0;

    /// <summary>Есть ли теги.</summary>
    public bool HasTags => Entry.Tags.Count > 0;

    /// <summary>
    /// Название вида файла по-русски.
    /// </summary>
    public static string KindName(FileKind kind) => kind switch
    {
        FileKind.Folder => "Папка",
        FileKind.Document => "Документ",
        FileKind.Spreadsheet => "Таблица",
        FileKind.Presentation => "Презентация",
        FileKind.Pdf => "PDF",
        FileKind.Image => "Изображение",
        FileKind.Video => "Видео",
        FileKind.Audio => "Аудио",
        FileKind.Archive => "Архив",
        FileKind.Installer => "Программа",
        FileKind.Code => "Код",
        FileKind.Text => "Текст",
        _ => "Файл",
    };
}

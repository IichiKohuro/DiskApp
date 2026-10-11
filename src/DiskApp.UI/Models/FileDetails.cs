using System.Globalization;

using DiskApp.UI.Localization;
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
        SizeText = entry.Kind == FileKind.Folder ? "—" : L.F("FileDetails.Bytes", Formatting.Size(entry.Size), entry.Size.ToString("N0", CultureInfo.CurrentCulture));
        ModifiedText = entry.Modified == default ? "—" : entry.Modified.ToLocalTime().ToString("d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture);
        Availability = entry.IsOffline ? L.T("FileDetails.DriveUnavailable")
            : entry.IsCloudOnly ? L.T("FileDetails.CloudOnlyOpensAfterDownload")
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
        FileKind.Folder => L.T("Common.Kind.Folder"),
        FileKind.Document => L.T("Common.Kind.Document"),
        FileKind.Spreadsheet => L.T("Common.Kind.Spreadsheet"),
        FileKind.Presentation => L.T("Common.Kind.Presentation"),
        FileKind.Pdf => "PDF",
        FileKind.Image => L.T("Common.Kind.Image"),
        FileKind.Video => L.T("Common.Kind.Video"),
        FileKind.Audio => L.T("Common.Kind.Audio"),
        FileKind.Archive => L.T("Common.Kind.Archive"),
        FileKind.Installer => L.T("Common.Kind.Program"),
        FileKind.Code => L.T("Common.Kind.Code"),
        FileKind.Text => L.T("Common.Kind.Text"),
        _ => L.T("Common.Kind.File"),
    };
}

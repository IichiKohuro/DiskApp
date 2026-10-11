using DiskApp.UI.Models;

namespace DiskApp.UI.Services;

/// <summary>
/// Макетный набор файлов для интерфейса до появления настоящего индекса: четыре корня, разные виды файлов,
/// русские и английские имена, избранное и теги. Даты задаются относительно момента создания,
/// чтобы подписи «2 ч назад» выглядели живыми.
/// </summary>
public static class SampleData
{
    private const string ProjectsRoot = @"D:\Projects";
    private const string DocumentsRoot = @"C:\Users\Иван\Documents";
    private const string DownloadsRoot = @"C:\Users\Иван\Downloads";
    private const string PhotosRoot = @"E:\Photos";

    private const long Kilobyte = 1024;
    private const long Megabyte = Kilobyte * 1024;
    private const long Gigabyte = Megabyte * 1024;

    /// <summary>Корни макетного индекса.</summary>
    public static IReadOnlyList<string> Roots { get; } = [ProjectsRoot, DocumentsRoot, DownloadsRoot, PhotosRoot];

    /// <summary>Сколько файлов ждёт разбора во «входящих» по макету.</summary>
    public static int InboxCount => 12;

    /// <summary>Общее число записей, которое показывает макетный индекс (больше, чем файлов в наборе, как у настоящего).</summary>
    public static long TotalEntries => 128_412;

    /// <summary>
    /// Записи макетного индекса с датами относительно <paramref name="now"/>.
    /// </summary>
    public static IReadOnlyList<FileEntry> CreateEntries(DateTimeOffset now)
    {
        FileEntry Entry(string root, string relativePath, FileKind kind, long size, TimeSpan age, bool favorite = false, params string[] tags)
            => new()
            {
                FullPath = root + '\\' + relativePath,
                Kind = kind,
                Root = root,
                Size = size,
                Modified = now - age,
                IsFavorite = favorite,
                Tags = tags,
            };

        static TimeSpan Hours(double hours) => TimeSpan.FromHours(hours);
        static TimeSpan Days(double days) => TimeSpan.FromDays(days);

        return
        [
            Entry(ProjectsRoot, @"DiskApp\src\DiskApp.UI\Views\MainView.axaml", FileKind.Code, 6 * Kilobyte, Hours(0.1)),
            Entry(ProjectsRoot, @"DiskApp\PLAN.md", FileKind.Text, 96 * Kilobyte, Hours(0.3)),
            Entry(ProjectsRoot, @"DiskApp\README.md", FileKind.Text, 24 * Kilobyte, Hours(1), favorite: true, "diskapp"),
            Entry(ProjectsRoot, @"DiskApp\src\DiskApp.Desktop\Program.cs", FileKind.Code, 3 * Kilobyte, Hours(2)),
            Entry(ProjectsRoot, @"DiskApp\docs\ARCHITECTURE.md", FileKind.Text, 88 * Kilobyte, Hours(5)),
            Entry(ProjectsRoot, @"DiskApp", FileKind.Folder, 0, Hours(0.1), favorite: true, "diskapp"),
            Entry(ProjectsRoot, @"ShopBot\appsettings.json", FileKind.Code, 1 * Kilobyte, Days(3)),
            Entry(ProjectsRoot, @"ShopBot\src\Bot.csproj", FileKind.Code, 2 * Kilobyte, Days(3)),
            Entry(ProjectsRoot, @"Portfolio\index.html", FileKind.Code, 14 * Kilobyte, Days(12)),
            Entry(ProjectsRoot, @"Portfolio\assets\hero.png", FileKind.Image, (long)(1.8 * Megabyte), Days(12)),
            Entry(ProjectsRoot, @"Archive\old-site-2023.zip", FileKind.Archive, 240 * Megabyte, Days(300)),

            Entry(DocumentsRoot, @"Отчёт за 3 квартал 2026.docx", FileKind.Document, (long)(1.2 * Megabyte), Days(1), tags: "работа"),
            Entry(DocumentsRoot, @"Отчёт за 2 квартал 2026.docx", FileKind.Document, (long)(1.1 * Megabyte), Days(95), tags: "работа"),
            Entry(DocumentsRoot, @"Бюджет 2026.xlsx", FileKind.Spreadsheet, 340 * Kilobyte, Days(2), favorite: true),
            Entry(DocumentsRoot, @"Презентация для инвесторов.pptx", FileKind.Presentation, 18 * Megabyte, Days(4), tags: "работа"),
            Entry(DocumentsRoot, @"Договор аренды.pdf", FileKind.Pdf, (long)(2.4 * Megabyte), Days(20), tags: ["дом", "документы"]),
            Entry(DocumentsRoot, @"Паспорт сканы.pdf", FileKind.Pdf, 6 * Megabyte, Days(60), tags: "документы"),
            Entry(DocumentsRoot, @"Заметки.txt", FileKind.Text, 3 * Kilobyte, Hours(0.5)),
            Entry(DocumentsRoot, @"Рецепты\Паста карбонара.md", FileKind.Text, 2 * Kilobyte, Days(8)),
            Entry(DocumentsRoot, @"Счета\Электричество сентябрь.pdf", FileKind.Pdf, 180 * Kilobyte, Days(6), tags: "счета"),
            Entry(DocumentsRoot, @"Счета\Интернет сентябрь.pdf", FileKind.Pdf, 120 * Kilobyte, Days(6), tags: "счета"),
            Entry(DocumentsRoot, @"Книги\Clean Architecture.epub", FileKind.Other, (long)(4.2 * Megabyte), Days(200)),
            Entry(DocumentsRoot, @"Резюме Иван Петров.pdf", FileKind.Pdf, 220 * Kilobyte, Days(40), favorite: true),

            Entry(DownloadsRoot, @"avalonia-12-release-notes.pdf", FileKind.Pdf, (long)(1.1 * Megabyte), Hours(2)),
            Entry(DownloadsRoot, @"VisualStudioSetup.exe", FileKind.Installer, 4 * Megabyte, Days(1)),
            Entry(DownloadsRoot, @"node-v24.9.0-x64.msi", FileKind.Installer, 32 * Megabyte, Days(3)),
            Entry(DownloadsRoot, @"invoice-10234.pdf", FileKind.Pdf, 90 * Kilobyte, Hours(0.8)),
            Entry(DownloadsRoot, @"screenshot-2026-10-10.png", FileKind.Image, 420 * Kilobyte, Hours(0.2)),
            Entry(DownloadsRoot, @"dataset.csv", FileKind.Spreadsheet, 52 * Megabyte, Days(5)),
            Entry(DownloadsRoot, @"music-mix.mp3", FileKind.Audio, 9 * Megabyte, Days(10)),
            Entry(DownloadsRoot, @"report_final_v3.docx", FileKind.Document, 800 * Kilobyte, Days(1)),
            Entry(DownloadsRoot, @"photos-trip.zip", FileKind.Archive, (long)(1.3 * Gigabyte), Days(2)),
            Entry(DownloadsRoot, @"Lecture 12 — Distributed Systems.mp4", FileKind.Video, (long)(1.9 * Gigabyte), Days(7)),

            Entry(PhotosRoot, @"2026\10\IMG_4521.jpg", FileKind.Image, (long)(3.2 * Megabyte), Days(1)),
            Entry(PhotosRoot, @"2026\10\IMG_4522.jpg", FileKind.Image, (long)(3.4 * Megabyte), Days(1)),
            Entry(PhotosRoot, @"2026\09\Крым\DSC_0871.jpg", FileKind.Image, 5 * Megabyte, Days(25), tags: "отпуск"),
            Entry(PhotosRoot, @"2026\09\Крым\DSC_0872.jpg", FileKind.Image, (long)(5.1 * Megabyte), Days(25), tags: "отпуск"),
            Entry(PhotosRoot, @"2026\09\Крым\DJI_0043.mp4", FileKind.Video, 780 * Megabyte, Days(25), tags: "отпуск") with { IsCloudOnly = true },
            Entry(PhotosRoot, @"2025\12\Новый год\IMG_3310.jpg", FileKind.Image, 4 * Megabyte, Days(290)),
            Entry(PhotosRoot, @"Избранное", FileKind.Folder, 0, Days(3), favorite: true),
            Entry(PhotosRoot, @"Portraits\Анна.png", FileKind.Image, (long)(2.2 * Megabyte), Days(120)),
        ];
    }

    /// <summary>
    /// Начальный список недавно открытых файлов: несколько записей из набора, самые свежие первыми.
    /// </summary>
    public static List<FileEntry> CreateRecent(IReadOnlyList<FileEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        string[] recentNames =
        [
            "MainView.axaml",
            "screenshot-2026-10-10.png",
            "invoice-10234.pdf",
            "Отчёт за 3 квартал 2026.docx",
            "Бюджет 2026.xlsx",
            "avalonia-12-release-notes.pdf",
        ];

        return recentNames
            .Select(name => entries.First(entry => entry.Name == name))
            .ToList();
    }

    /// <summary>
    /// Макетное состояние индекса: актуален, обновлён две минуты назад.
    /// </summary>
    public static IndexStatus CreateIndexStatus(IReadOnlyList<FileEntry> entries, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return new IndexStatus
        {
            TotalEntries = TotalEntries,
            Roots = Roots,
            IsIndexing = false,
            Progress = 1,
            LastUpdated = now - TimeSpan.FromMinutes(2),
            InboxCount = InboxCount,
        };
    }
}

using DiskApp.UI.Text;

namespace DiskApp.UI.Models;

/// <summary>
/// Вид файла для иконки, фильтров и подсказок. Определяется по расширению; «папка» — отдельный вид.
/// </summary>
public enum FileKind
{
    /// <summary>Папка.</summary>
    Folder,

    /// <summary>Текстовый документ: docx, odt, rtf.</summary>
    Document,

    /// <summary>Таблица: xlsx, csv, ods.</summary>
    Spreadsheet,

    /// <summary>Презентация: pptx, key.</summary>
    Presentation,

    /// <summary>PDF.</summary>
    Pdf,

    /// <summary>Изображение.</summary>
    Image,

    /// <summary>Видео.</summary>
    Video,

    /// <summary>Аудио.</summary>
    Audio,

    /// <summary>Архив.</summary>
    Archive,

    /// <summary>Установщик или исполняемый файл.</summary>
    Installer,

    /// <summary>Исходный код и конфигурация.</summary>
    Code,

    /// <summary>Простой текст и заметки.</summary>
    Text,

    /// <summary>Всё остальное.</summary>
    Other,
}

/// <summary>
/// Диапазон подсветки совпадения в тексте (индексы символов UTF-16).
/// </summary>
/// <param name="Start">Начало.</param>
/// <param name="Length">Длина.</param>
public readonly record struct HighlightRange(int Start, int Length);

/// <summary>
/// Запись индекса в том виде, в каком её показывает интерфейс. До появления контрактов API (этап 4–5)
/// это единственная модель файла в <c>DiskApp.UI</c>; затем она заполняется из DTO клиента.
/// </summary>
public sealed record FileEntry
{
    /// <summary>Идентификатор записи на сервере; 0 — запись макета.</summary>
    public long Id { get; init; }

    /// <summary>Ключ иконки сервера (<c>folder</c>, <c>ext:pdf</c>, <c>entry:42</c>); пусто — иконка по виду.</summary>
    public string IconKey { get; init; } = string.Empty;

    /// <summary>Совпадения в имени, найденные сервером (в том числе нечёткие); <c>null</c> — подсвечивать слова запроса.</summary>
    public IReadOnlyList<HighlightRange>? NameHighlights { get; init; }

    /// <summary>Полный путь. Разделитель — как в индексе; интерфейс не трогает его, чтобы не ломать сетевые и портативные пути.</summary>
    public required string FullPath { get; init; }

    /// <summary>Вид файла для иконки и фильтров.</summary>
    public required FileKind Kind { get; init; }

    /// <summary>Корень индекса, в котором лежит файл.</summary>
    public required string Root { get; init; }

    /// <summary>Размер в байтах; для папок — 0.</summary>
    public long Size { get; init; }

    /// <summary>Время последнего изменения.</summary>
    public DateTimeOffset Modified { get; init; }

    /// <summary>Файл добавлен в избранное.</summary>
    public bool IsFavorite { get; init; }

    /// <summary>Теги пользователя.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Цвета тегов по имени (<c>#RRGGBB</c>); у тега без цвета записи нет.</summary>
    public IReadOnlyDictionary<string, string> TagColors { get; init; } = NoColors;

    /// <summary>Теги с цветами — для чипов в списках и карточке.</summary>
    public IReadOnlyList<TagLabel> TagLabels => [.. Tags.Select(name => new TagLabel(name, TagColors.GetValueOrDefault(name)))];

    /// <summary>Облачная заглушка (OneDrive и т. п.): содержимого на диске нет, открытие его скачает.</summary>
    public bool IsCloudOnly { get; init; }

    /// <summary>Корень файла сейчас недоступен: диск отключён или пропала сеть.</summary>
    public bool IsOffline { get; init; }

    /// <summary>Имя файла с расширением.</summary>
    public string Name => PathText.FileName(FullPath);

    /// <summary>Папка, в которой лежит файл, без завершающего разделителя.</summary>
    public string Directory => PathText.DirectoryName(FullPath);

    /// <summary>Расширение без точки в нижнем регистре; пусто, если расширения нет.</summary>
    public string Extension => PathText.Extension(FullPath);

    private static IReadOnlyDictionary<string, string> NoColors { get; } = new Dictionary<string, string>();
}

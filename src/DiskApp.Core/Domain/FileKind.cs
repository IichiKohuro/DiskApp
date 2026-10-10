namespace DiskApp.Core.Domain;

/// <summary>
/// Вид записи для иконок, фильтра <c>type:</c> и подсказок разбора. Определяется по расширению через <see cref="FileKindMap"/>.
/// </summary>
public enum FileKind
{
    /// <summary>Расширение неизвестно или его нет.</summary>
    Other,

    /// <summary>Папка.</summary>
    Folder,

    /// <summary>Изображение: jpg, png, heic, svg, raw-форматы.</summary>
    Image,

    /// <summary>Видео: mp4, mkv, mov.</summary>
    Video,

    /// <summary>Аудио: mp3, flac, wav.</summary>
    Audio,

    /// <summary>Текстовый документ: docx, odt, rtf, txt, md.</summary>
    Document,

    /// <summary>Таблица: xlsx, csv, ods.</summary>
    Spreadsheet,

    /// <summary>Презентация: pptx, odp, key.</summary>
    Presentation,

    /// <summary>PDF.</summary>
    Pdf,

    /// <summary>Архив и образ диска: zip, 7z, rar, iso.</summary>
    Archive,

    /// <summary>Исходный код, разметка и конфигурация.</summary>
    Code,

    /// <summary>Исполняемый файл, установщик, библиотека.</summary>
    Executable,

    /// <summary>Ярлык: lnk, url.</summary>
    Shortcut,
}

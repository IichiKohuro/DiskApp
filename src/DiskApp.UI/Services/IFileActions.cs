using DiskApp.UI.Models;

namespace DiskApp.UI.Services;

/// <summary>
/// Действия над файлами из результатов поиска (PLAN.md, этап 7.3). Действия оболочки и файловые операции выполняет
/// сервер (он владеет файловой системой), буфер обмена — интерфейс. Ошибка — исключение с понятным сообщением:
/// его показывает уведомление.
/// </summary>
public interface IFileActions
{
    /// <summary>Системный диалог «Открыть с помощью».</summary>
    Task OpenWithAsync(FileEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Открыть Проводник с выделенным файлом.</summary>
    Task RevealAsync(FileEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Окно свойств файла.</summary>
    Task ShowPropertiesAsync(FileEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Запустить от имени администратора.</summary>
    Task RunAsAdminAsync(FileEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Скопировать файлы в буфер обмена, как это делает Проводник.</summary>
    Task CopyFilesAsync(IReadOnlyList<FileEntry> entries, CancellationToken cancellationToken = default);

    /// <summary>Скопировать полные пути текстом, по одному в строке.</summary>
    Task CopyPathsAsync(IReadOnlyList<FileEntry> entries, CancellationToken cancellationToken = default);

    /// <summary>Переместить в корзину; завершается, когда операция выполнена.</summary>
    Task MoveToRecycleBinAsync(IReadOnlyList<FileEntry> entries, CancellationToken cancellationToken = default);

    /// <summary>
    /// Переименовать; завершается, когда операция выполнена. Возвращает запись с новым именем.
    /// </summary>
    Task<FileEntry> RenameAsync(FileEntry entry, string newName, CancellationToken cancellationToken = default);

    /// <summary>Добавить в избранное (закрепить) или убрать оттуда. Возвращает обновлённую запись.</summary>
    Task<FileEntry> SetFavoriteAsync(FileEntry entry, bool favorite, CancellationToken cancellationToken = default);

    /// <summary>
    /// Скопировать файлы с диска в папку из результатов (перетаскивание в окно поиска); при совпадении имён сохраняются
    /// оба файла. Возвращает, сколько файлов сохранено.
    /// </summary>
    Task<int> CopyIntoAsync(IReadOnlyList<string> files, FileEntry folder, CancellationToken cancellationToken = default);
}

/// <summary>
/// Буфер обмена: реализует оболочка (десктоп — через буфер Windows, с файлами в формате Проводника).
/// </summary>
public interface IClipboardService
{
    /// <summary>Положить текст.</summary>
    Task SetTextAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Положить файлы: вставка в Проводнике скопирует их.</summary>
    Task SetFilesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
}

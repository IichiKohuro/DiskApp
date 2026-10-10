using DiskApp.Core.Results;

namespace DiskApp.Core.Abstractions;

/// <summary>
/// Действия оболочки ОС над файлами (docs/ARCHITECTURE.md, раздел 10). Пути абсолютные и нормализованные.
/// Удаление только в корзину (решение 16 плана); ошибки возвращаются значением.
/// </summary>
public interface IPlatformShell
{
    /// <summary>Открыть файл или папку приложением по умолчанию.</summary>
    Task<Result> OpenAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Показать системный диалог «Открыть с помощью».</summary>
    Task<Result> OpenWithAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Открыть Проводник с выделенным файлом.</summary>
    Task<Result> RevealInExplorerAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Показать окно свойств.</summary>
    Task<Result> ShowPropertiesAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Переместить в корзину с возможностью восстановления.</summary>
    Task<Result> MoveToRecycleBinAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>Запустить от имени администратора (запрос повышения прав).</summary>
    Task<Result> RunAsAdminAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Скопировать пути в буфер обмена как текст, по одному в строке.</summary>
    Task<Result> CopyPathsToClipboardAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>Скопировать файлы в буфер обмена, как это делает Проводник.</summary>
    Task<Result> CopyFilesToClipboardAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);
}

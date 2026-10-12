namespace DiskApp.Core.Abstractions;

/// <summary>
/// Кто держит файл открытым: файловая операция не смогла его перенести или удалить, потому что он занят, и пользователь
/// должен знать, какую программу закрыть («открыт в Microsoft Excel»), а не читать «используется другим процессом».
/// </summary>
public interface IFileLockInspector
{
    /// <summary>
    /// Названия программ, которые сейчас держат файл; пусто — неизвестно или никто. Не бросает исключений.
    /// </summary>
    /// <param name="path">Полный путь к файлу.</param>
    IReadOnlyList<string> FindLockingApplications(string path);
}

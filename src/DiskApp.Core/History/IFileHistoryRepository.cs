namespace DiskApp.Core.History;

/// <summary>
/// Хранилище истории файлов (PLAN.md, этап 20): события новые сверху, срок хранения и предел размера.
/// </summary>
public interface IFileHistoryRepository
{
    /// <summary>Записывает события; номера и время задаёт хранилище и вызывающий.</summary>
    /// <returns>Записанные события с номерами.</returns>
    Task<IReadOnlyList<FileEvent>> AddAsync(IReadOnlyList<FileEvent> events, CancellationToken cancellationToken = default);

    /// <summary>События по отбору, новые сверху.</summary>
    Task<IReadOnlyList<FileEvent>> QueryAsync(FileHistoryQuery query, CancellationToken cancellationToken = default);

    /// <summary>Событие по номеру.</summary>
    Task<FileEvent?> GetAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>События, где файл после события лежит по этому пути или лежал до него, новые сверху.</summary>
    Task<IReadOnlyList<FileEvent>> GetByPathAsync(string path, int limit, CancellationToken cancellationToken = default);

    /// <summary>Удаляет события старше момента; возвращает, сколько удалено.</summary>
    Task<int> DeleteOlderThanAsync(DateTimeOffset utc, CancellationToken cancellationToken = default);

    /// <summary>Оценка места, которое занимает история, в байтах.</summary>
    Task<long> EstimateSizeAsync(CancellationToken cancellationToken = default);

    /// <summary>Удаляет самые старые события; возвращает, сколько удалено.</summary>
    Task<int> DeleteOldestAsync(int count, CancellationToken cancellationToken = default);
}

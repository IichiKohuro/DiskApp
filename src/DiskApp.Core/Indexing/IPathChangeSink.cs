namespace DiskApp.Core.Indexing;

/// <summary>
/// Приёмник сообщений «эти пути изменились»: файловые операции сообщают о папках, которые затронули, и индекс
/// сверяет их сразу, не дожидаясь наблюдателя (PLAN.md, этап 4.5).
/// </summary>
public interface IPathChangeSink
{
    /// <summary>
    /// Пути (абсолютные, нормализованные) изменились; сверить содержащие их папки.
    /// </summary>
    void OnPathsChanged(IReadOnlyCollection<string> absolutePaths);
}

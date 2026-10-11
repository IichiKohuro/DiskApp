using DiskApp.UI.Models;

namespace DiskApp.UI.Services;

/// <summary>
/// Корни индекса для главной страницы (PLAN.md, этап 8.2): список с состоянием, сводка, действия, добавление.
/// </summary>
public interface IRootsService
{
    /// <summary>Корни с текущим состоянием индексации.</summary>
    Task<IReadOnlyList<RootInfo>> GetRootsAsync(CancellationToken cancellationToken = default);

    /// <summary>Сводка корня: файлы, папки, размер.</summary>
    Task<RootSummary> GetSummaryAsync(long rootId, CancellationToken cancellationToken = default);

    /// <summary>Добавляет папку в индекс; возвращает корень.</summary>
    Task<RootInfo> AddAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Ставит полное сканирование корня в очередь.</summary>
    Task RescanAsync(long rootId, CancellationToken cancellationToken = default);

    /// <summary>Приостанавливает индексацию корня.</summary>
    Task PauseAsync(long rootId, CancellationToken cancellationToken = default);

    /// <summary>Возобновляет индексацию корня.</summary>
    Task ResumeAsync(long rootId, CancellationToken cancellationToken = default);

    /// <summary>Убирает корень из индекса; файлы на диске не трогаются.</summary>
    Task RemoveAsync(long rootId, CancellationToken cancellationToken = default);

    /// <summary>Меняет параметры корня: подпись, скрытые и системные, глубину; индекс пересобирается сервером.</summary>
    Task<RootInfo> UpdateAsync(RootInfo root, CancellationToken cancellationToken = default);
}

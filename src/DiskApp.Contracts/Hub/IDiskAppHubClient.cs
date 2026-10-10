using DiskApp.Contracts.App;
using DiskApp.Contracts.Entries;
using DiskApp.Contracts.Operations;
using DiskApp.Contracts.Roots;

namespace DiskApp.Contracts.Hub;

/// <summary>
/// События, которые сервер отправляет клиентам через хаб <see cref="HubRoutes.Path"/> (PLAN.md, этап 4.4).
/// Имена методов — имена сообщений SignalR.
/// </summary>
public interface IDiskAppHubClient
{
    /// <summary>Ход индексации корня; не чаще четырёх раз в секунду на корень.</summary>
    Task OnIndexProgress(IndexProgressDto progress);

    /// <summary>Смена состояния корня.</summary>
    Task OnRootStatusChanged(RootStatusChangedDto change);

    /// <summary>Пакет изменений записей корня; только подписчикам корня (<see cref="HubRoutes.SubscribeRoot"/>).</summary>
    Task OnEntriesChanged(EntriesChangedDto batch);

    /// <summary>Ход файловой операции.</summary>
    Task OnOperationProgress(OperationProgressDto progress);

    /// <summary>Файловая операция завершена.</summary>
    Task OnOperationCompleted(OperationCompletedDto completed);

    /// <summary>Файловая операция ждёт решения по конфликту.</summary>
    Task OnOperationNeedsDecision(ConflictDto conflict);

    /// <summary>Настройки изменены.</summary>
    Task OnSettingsChanged(SettingsDto settings);
}

/// <summary>
/// Ответ на <see cref="HubRoutes.GetStatus"/> и первое сообщение после переподключения: состояние индексации и активные операции.
/// </summary>
/// <param name="Index">Состояние индексации.</param>
/// <param name="Operations">Незавершённые операции.</param>
public sealed record HubStatusDto(IndexStatusDto Index, IReadOnlyList<OperationDto> Operations);

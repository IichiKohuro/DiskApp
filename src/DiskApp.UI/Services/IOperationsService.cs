using DiskApp.UI.Models;

namespace DiskApp.UI.Services;

/// <summary>
/// Очередь файловых операций сервера для интерфейса (PLAN.md, этап 8.4): список, постановка, отмена, возврат
/// результата, решение по конфликту и события хода. События приходят на фоновом потоке.
/// </summary>
public interface IOperationsService
{
    /// <summary>Операция добавлена или изменилась (ход, состояние).</summary>
    event EventHandler<OperationInfo>? Updated;

    /// <summary>Операция ждёт решения по конфликту имён.</summary>
    event EventHandler<ConflictInfo>? ConflictRaised;

    /// <summary>Операции сеанса: идущие и завершённые.</summary>
    Task<IReadOnlyList<OperationInfo>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Ставит операцию в очередь; конфликты имён спрашиваются (<see cref="ConflictRaised"/>).</summary>
    Task<OperationInfo> EnqueueAsync(NewOperation operation, CancellationToken cancellationToken = default);

    /// <summary>Разбор: переносит из накопителя в рабочую папку с тегами; одинаковые имена — «оставить оба».</summary>
    Task<OperationInfo> SortAsync(SortOperation operation, CancellationToken cancellationToken = default);

    /// <summary>Отменяет операцию.</summary>
    Task CancelAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Отменяет результат выполненной операции; возвращает обратную операцию.</summary>
    Task<OperationInfo> UndoAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Передаёт решение по конфликту.</summary>
    Task DecideAsync(Guid id, ConflictAnswer answer, CancellationToken cancellationToken = default);
}

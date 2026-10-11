using DiskApp.UI.Models;

namespace DiskApp.UI.Services;

/// <summary>
/// Очередь операций в памяти для превью и тестов: операции не выполняются сами, их ход и итог задаёт тест
/// (<see cref="Report"/>, <see cref="RaiseConflict"/>).
/// </summary>
public sealed class SampleOperationsService : IOperationsService
{
    private readonly List<OperationInfo> _operations = [];
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public event EventHandler<OperationInfo>? Updated;

    /// <inheritdoc />
    public event EventHandler<ConflictInfo>? ConflictRaised;

    /// <summary>Решения по конфликтам: операция и ответ.</summary>
    public List<(Guid Id, ConflictAnswer Answer)> Decisions { get; } = [];

    /// <summary>Отменённые операции.</summary>
    public List<Guid> Cancelled { get; } = [];

    /// <summary>Операции, результат которых отменили.</summary>
    public List<Guid> Undone { get; } = [];

    /// <inheritdoc />
    public Task<IReadOnlyList<OperationInfo>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<OperationInfo>>([.. _operations]);
        }
    }

    /// <inheritdoc />
    public Task<OperationInfo> EnqueueAsync(NewOperation operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var info = new OperationInfo
        {
            Id = Guid.NewGuid(),
            Kind = operation.Kind,
            State = FileOperationState.Queued,
            Sources = operation.Sources,
            Destination = operation.Destination,
            Created = DateTimeOffset.Now,
            Message = operation.NewName,
        };
        Report(info);
        return Task.FromResult(info);
    }

    /// <inheritdoc />
    public Task CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Cancelled.Add(id);
        Change(id, static info => info with { State = FileOperationState.Cancelled });
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<OperationInfo> UndoAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Undone.Add(id);
        Change(id, static info => info with { CanUndo = false });
        var undo = new OperationInfo { Id = Guid.NewGuid(), Kind = FileOperationKind.Move, State = FileOperationState.Completed, Created = DateTimeOffset.Now };
        Report(undo);
        return Task.FromResult(undo);
    }

    /// <inheritdoc />
    public Task DecideAsync(Guid id, ConflictAnswer answer, CancellationToken cancellationToken = default)
    {
        Decisions.Add((id, answer));
        Change(id, static info => info with { State = FileOperationState.Running, PendingConflict = null });
        return Task.CompletedTask;
    }

    /// <summary>Сообщает новое состояние операции, как это делает хаб.</summary>
    public void Report(OperationInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        lock (_gate)
        {
            var index = _operations.FindIndex(item => item.Id == info.Id);
            if (index >= 0)
            {
                _operations[index] = info;
            }
            else
            {
                _operations.Add(info);
            }
        }

        Updated?.Invoke(this, info);
    }

    /// <summary>Сообщает конфликт имён, как это делает хаб.</summary>
    public void RaiseConflict(ConflictInfo conflict)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        Change(conflict.OperationId, info => info with { State = FileOperationState.WaitingDecision, PendingConflict = conflict });
        ConflictRaised?.Invoke(this, conflict);
    }

    private void Change(Guid id, Func<OperationInfo, OperationInfo> change)
    {
        OperationInfo? updated = null;
        lock (_gate)
        {
            var index = _operations.FindIndex(item => item.Id == id);
            if (index >= 0)
            {
                updated = _operations[index] = change(_operations[index]);
            }
        }

        if (updated is not null)
        {
            Updated?.Invoke(this, updated);
        }
    }
}

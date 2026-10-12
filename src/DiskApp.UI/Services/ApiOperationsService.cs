using System.Collections.Concurrent;

using DiskApp.Client;
using DiskApp.Contracts.Operations;
using DiskApp.UI.Models;

namespace DiskApp.UI.Services;

/// <summary>
/// <see cref="IOperationsService"/> поверх клиента API и хаба: ход приходит событиями хаба, по завершении операция
/// перечитывается целиком (итог, возможность отмены результата).
/// </summary>
public sealed class ApiOperationsService : IOperationsService
{
    private readonly IDiskAppApi _api;
    private readonly ConcurrentDictionary<Guid, OperationInfo> _known = new();

    /// <summary>
    /// Создаёт сервис и подписывается на события операций хаба.
    /// </summary>
    public ApiOperationsService(IDiskAppApi api, DiskAppHubConnection hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        _api = api ?? throw new ArgumentNullException(nameof(api));
        hub.OperationProgress += OnProgress;
        hub.OperationCompleted += completed => _ = RefreshAsync(completed.OperationId);
        hub.OperationNeedsDecision += OnConflict;
    }

    /// <inheritdoc />
    public event EventHandler<OperationInfo>? Updated;

    /// <inheritdoc />
    public event EventHandler<ConflictInfo>? ConflictRaised;

    /// <inheritdoc />
    public async Task<IReadOnlyList<OperationInfo>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var operations = await _api.GetOperationsAsync(cancellationToken).ConfigureAwait(false);
        return [.. operations.Select(dto => Remember(ToInfo(dto)))];
    }

    /// <inheritdoc />
    public async Task<OperationInfo> EnqueueAsync(NewOperation operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var request = new OperationRequest
        {
            Kind = (OperationKind)(int)operation.Kind,
            Sources = operation.Sources,
            Destination = operation.Destination,
            NewName = operation.NewName,
            Conflicts = ConflictPolicy.Ask,
        };
        var info = Remember(ToInfo(await _api.EnqueueOperationAsync(request, cancellationToken).ConfigureAwait(false)));
        Updated?.Invoke(this, info);
        return info;
    }

    /// <inheritdoc />
    public async Task<OperationInfo> SortAsync(SortOperation operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var request = new SortFilesRequest
        {
            Sources = operation.Sources,
            Destination = operation.Destination,
            NewFolder = operation.NewFolder,
            Tags = operation.Tags,
            Names = operation.Names,
        };
        var info = Remember(ToInfo(await _api.SortFilesAsync(request, cancellationToken).ConfigureAwait(false)));
        Updated?.Invoke(this, info);
        return info;
    }

    /// <inheritdoc />
    public async Task<OperationInfo> DropAsync(DropOperation operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var request = new DropFilesRequest { Sources = operation.Sources, Destination = operation.Destination, Copy = operation.Copy };
        var info = Remember(ToInfo(await _api.DropFilesAsync(request, cancellationToken).ConfigureAwait(false)));
        Updated?.Invoke(this, info);
        return info;
    }

    /// <inheritdoc />
    public Task CancelAsync(Guid id, CancellationToken cancellationToken = default) => _api.CancelOperationAsync(id, cancellationToken);

    /// <inheritdoc />
    public async Task<OperationInfo> UndoAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var info = Remember(ToInfo(await _api.UndoOperationAsync(id, cancellationToken).ConfigureAwait(false)));
        Updated?.Invoke(this, info);
        if (_known.TryGetValue(id, out var original))
        {
            // Результат отменён: второй раз отменять нечего.
            Updated?.Invoke(this, Remember(original with { CanUndo = false }));
        }

        return info;
    }

    /// <inheritdoc />
    public Task DecideAsync(Guid id, ConflictAnswer answer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(answer);
        return _api.DecideOperationAsync(id, new ConflictDecisionDto { Resolution = (ConflictResolution)(int)answer.Choice, ApplyToAll = answer.ApplyToAll }, cancellationToken);
    }

    /// <summary>
    /// Операция API в модель интерфейса.
    /// </summary>
    public static OperationInfo ToInfo(OperationDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        return new OperationInfo
        {
            Id = dto.Id,
            Kind = (FileOperationKind)(int)dto.Kind,
            State = (FileOperationState)(int)dto.State,
            Sources = dto.Sources,
            Destination = dto.Destination,
            Created = dto.CreatedUtc,
            Completed = dto.CompletedUtc,
            ProcessedItems = dto.Progress.ProcessedItems,
            TotalItems = dto.Progress.TotalItems,
            ProcessedBytes = dto.Progress.ProcessedBytes,
            TotalBytes = dto.Progress.TotalBytes,
            CurrentPath = dto.Progress.CurrentPath,
            BytesPerSecond = dto.Progress.BytesPerSecond,
            RemainingSeconds = dto.Progress.RemainingSeconds,
            Message = dto.Message,
            CanUndo = dto.CanUndo,
            PendingConflict = dto.PendingConflict is { } conflict ? ToInfo(conflict) : null,
            IsAutoSort = dto.Origin == OperationOrigin.AutoSort,
        };
    }

    private static ConflictInfo ToInfo(ConflictDto conflict)
        => new(conflict.OperationId, conflict.SourcePath, conflict.TargetPath, conflict.SourceSize, conflict.SourceModifiedUtc, conflict.TargetSize, conflict.TargetModifiedUtc);

    private OperationInfo Remember(OperationInfo info) => _known[info.Id] = info;

    private void OnProgress(OperationProgressDto progress)
    {
        if (!_known.TryGetValue(progress.OperationId, out var info))
        {
            _ = RefreshAsync(progress.OperationId);
            return;
        }

        Updated?.Invoke(this, Remember(info with
        {
            State = FileOperationState.Running,
            ProcessedItems = progress.ProcessedItems,
            TotalItems = progress.TotalItems,
            ProcessedBytes = progress.ProcessedBytes,
            TotalBytes = progress.TotalBytes,
            CurrentPath = progress.CurrentPath,
            BytesPerSecond = progress.BytesPerSecond,
            RemainingSeconds = progress.RemainingSeconds,
        }));
    }

    private void OnConflict(ConflictDto dto)
    {
        var conflict = ToInfo(dto);
        if (_known.TryGetValue(dto.OperationId, out var info))
        {
            Updated?.Invoke(this, Remember(info with { State = FileOperationState.WaitingDecision, PendingConflict = conflict }));
        }

        ConflictRaised?.Invoke(this, conflict);
    }

    private async Task RefreshAsync(Guid id)
    {
        try
        {
            var info = Remember(ToInfo(await _api.GetOperationAsync(id).ConfigureAwait(false)));
            Updated?.Invoke(this, info);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Операцию могли убрать из списка сервера; следующая загрузка списка всё покажет.
        }
    }
}

using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DiskApp.UI.Localization;
using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.Text;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.ViewModels;

/// <summary>
/// Страница «Операции» и координатор файловых операций интерфейса (PLAN.md, этап 8.4): очередь с ходом, отмена,
/// возврат результата (`Ctrl+Z` — последняя операция), диалог конфликта имён, уведомление «Отменить» после
/// операций, начатых в интерфейсе, сводка для полосы главного окна и подсказки трея.
/// </summary>
public sealed partial class OperationsViewModel : PageViewModel, IOperationsCoordinator
{
    private readonly IOperationsService _operations;
    private readonly IDialogService? _dialogs;
    private readonly IInfoBarService? _notices;
    private readonly IDispatcher _dispatcher;
    private readonly HashSet<Guid> _started = [];
    private readonly HashSet<Guid> _resolving = [];
    private Guid? _lastUndoable;

    /// <summary>
    /// Создаёт страницу; без диалогов конфликты отменяют операцию, без уведомлений итог виден только в списке (тесты, превью).
    /// </summary>
    public OperationsViewModel(IOperationsService operations, IDispatcher dispatcher, IDialogService? dialogs = null, IInfoBarService? notices = null)
        : base(PageKeys.Operations, "Operations.Operations", FASymbol.Sync)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _operations = operations;
        _dialogs = dialogs;
        _notices = notices;
        _dispatcher = dispatcher;
        _operations.Updated += (_, info) => _dispatcher.Post(() => Upsert(info));
        _operations.ConflictRaised += (_, conflict) => _dispatcher.Post(() => _ = ResolveAsync(conflict));
        Initialization = LoadAsync();
    }

    /// <inheritdoc />
    public event EventHandler<OperationInfo>? OperationFinished;

    /// <summary>Первая загрузка списка.</summary>
    public Task Initialization { get; }

    /// <summary>Операции, новые сверху.</summary>
    public ObservableCollection<OperationItemViewModel> Items { get; } = [];

    /// <summary>Сколько операций идёт.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActive))]
    public partial int ActiveCount { get; set; }

    /// <summary>Сводка идущих операций: «2 операции · 45 %».</summary>
    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    /// <summary>Идут операции.</summary>
    public bool HasActive => ActiveCount > 0;

    /// <summary>Есть ли завершённые, которые можно убрать из списка.</summary>
    public bool HasFinished => Items.Any(static item => !item.IsActive);

    /// <summary>Списка нет: пустое состояние.</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <inheritdoc />
    public async Task<OperationInfo?> EnqueueAsync(NewOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            var info = await _operations.EnqueueAsync(operation);
            _started.Add(info.Id);
            Upsert(info);
            return info;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show(L.T("Operations.TheOperationWasNotStarted"), exception.Message, FAInfoBarSeverity.Error);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> UndoLastAsync()
    {
        if (_lastUndoable is not { } id || Items.FirstOrDefault(item => item.Info.Id == id) is not { CanUndo: true } item)
        {
            _notices?.Show(L.T("Operations.NothingToUndo"), L.T("Operations.RenameMoveAndCreateCanBe"), FAInfoBarSeverity.Informational);
            return false;
        }

        await UndoAsync(item);
        return true;
    }

    /// <summary>Отменить операцию.</summary>
    [RelayCommand]
    private async Task CancelAsync(OperationItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            await _operations.CancelAsync(item.Info.Id);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show(L.T("Operations.CouldNotCancel"), exception.Message, FAInfoBarSeverity.Error);
        }
    }

    /// <summary>Отменить результат операции.</summary>
    [RelayCommand]
    private async Task UndoAsync(OperationItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            var undo = await _operations.UndoAsync(item.Info.Id);
            item.Info = item.Info with { CanUndo = false };
            if (_lastUndoable == item.Info.Id)
            {
                _lastUndoable = null;
            }

            Upsert(undo);
            _notices?.Show(L.T("Operations.Undone"), OperationItemViewModel.Describe(item.Info), FAInfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show(L.T("Operations.CouldNotUndoTheResult"), exception.Message, FAInfoBarSeverity.Error);
        }
    }

    /// <summary>Убрать из списка завершённые операции.</summary>
    [RelayCommand]
    private void ClearFinished()
    {
        foreach (var item in Items.Where(static item => !item.IsActive).ToList())
        {
            Items.Remove(item);
        }

        UpdateSummary();
    }

    private async Task LoadAsync()
    {
        try
        {
            foreach (var info in (await _operations.GetAllAsync()).OrderBy(static info => info.Created))
            {
                Upsert(info);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Сервер ещё запускается: операции появятся по событиям.
            _ = exception;
        }
    }

    private void Upsert(OperationInfo info)
    {
        var item = Items.FirstOrDefault(candidate => candidate.Info.Id == info.Id);
        var wasActive = item?.IsActive ?? true;
        if (item is null)
        {
            item = new OperationItemViewModel(info);
            Items.Insert(0, item);
        }
        else
        {
            item.Info = info;
        }

        if (info.State == FileOperationState.Completed && info.CanUndo)
        {
            _lastUndoable = info.Id;
        }

        if (wasActive && !info.IsActive && _started.Remove(info.Id))
        {
            Notify(item);
        }

        if (wasActive && !info.IsActive)
        {
            OperationFinished?.Invoke(this, info);
        }

        UpdateSummary();
    }

    private void Notify(OperationItemViewModel item)
    {
        var info = item.Info;
        var title = OperationItemViewModel.Describe(info);
        switch (info.State)
        {
            case FileOperationState.Completed when item.CanUndo:
                _notices?.Show(L.T("Common.Done"), title, FAInfoBarSeverity.Success, L.T("Operations.Undo"), () => _ = UndoAsync(item));
                break;
            case FileOperationState.Completed:
                _notices?.Show(L.T("Common.Done"), title, FAInfoBarSeverity.Success);
                break;
            case FileOperationState.Failed:
                _notices?.Show(L.T("Operations.TheOperationFailed"), title + (info.Message is { Length: > 0 } message ? ": " + message : string.Empty), FAInfoBarSeverity.Error);
                break;
        }
    }

    private async Task ResolveAsync(ConflictInfo conflict)
    {
        // Переименование из быстрого поиска само отменяет операцию при конфликте — его не спрашиваем.
        if (Items.FirstOrDefault(item => item.Info.Id == conflict.OperationId)?.Info.Kind == FileOperationKind.Rename
            || !_resolving.Add(conflict.OperationId))
        {
            return;
        }

        try
        {
            var answer = _dialogs is null ? new ConflictAnswer(ConflictChoice.Cancel, false) : await _dialogs.ResolveConflictAsync(conflict);
            await _operations.DecideAsync(conflict.OperationId, answer);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show(L.T("Operations.CouldNotSendTheDecision"), exception.Message, FAInfoBarSeverity.Error);
        }
        finally
        {
            _resolving.Remove(conflict.OperationId);
        }
    }

    private void UpdateSummary()
    {
        var active = Items.Where(static item => item.IsActive).ToList();
        ActiveCount = active.Count;
        BadgeValue = active.Count;
        var fractions = active.Select(static item => item.Info.Fraction).OfType<double>().ToList();
        Summary = active.Count == 0 ? string.Empty
            : Formatting.Count(active.Count, L.T("Operations.Operation.One"), L.T("Operations.Operation.Few"), L.T("Operations.Operation.Many"))
                + (fractions.Count > 0 ? " · " + Formatting.Percent(fractions.Average()) : string.Empty);
        OnPropertyChanged(nameof(HasFinished));
        OnPropertyChanged(nameof(IsEmpty));
    }
}

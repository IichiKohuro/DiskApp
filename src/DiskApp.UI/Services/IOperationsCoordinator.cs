using DiskApp.UI.Models;

namespace DiskApp.UI.Services;

/// <summary>
/// Постановка операций от имени интерфейса (PLAN.md, этап 8.4): конфликты спрашиваются, итог показывается
/// уведомлением с «Отменить». Реализует страница «Операции».
/// </summary>
public interface IOperationsCoordinator
{
    /// <summary>Операция закончилась (выполнена, ошибка, отмена) — страницам пора перечитать папку.</summary>
    event EventHandler<OperationInfo>? OperationFinished;

    /// <summary>Ставит операцию в очередь; <c>null</c> — сервер отказал (причина показана уведомлением).</summary>
    Task<OperationInfo?> EnqueueAsync(NewOperation operation);

    /// <summary>Отменяет результат последней операции, которую можно отменить.</summary>
    Task<bool> UndoLastAsync();
}

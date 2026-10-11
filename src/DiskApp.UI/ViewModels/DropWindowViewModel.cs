using Avalonia.Input;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DiskApp.UI.Localization;
using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.Text;

namespace DiskApp.UI.ViewModels;

/// <summary>Что сделать с брошенными файлами.</summary>
public enum DropMode
{
    /// <summary>Перенести.</summary>
    Move,

    /// <summary>Скопировать.</summary>
    Copy,
}

/// <summary>Почему файлы сюда не бросить.</summary>
public enum DropRefusal
{
    /// <summary>Бросить можно.</summary>
    None,

    /// <summary>Папки-накопителя нет.</summary>
    NoInbox,

    /// <summary>Все файлы уже лежат в накопителе.</summary>
    AlreadyThere,

    /// <summary>Папку нельзя бросить в неё саму.</summary>
    IntoItself,

    /// <summary>Источник перетаскивания не разрешает ни копию, ни перенос.</summary>
    NotAllowed,
}

/// <summary>Решение о броске: действие или причина отказа.</summary>
/// <param name="Mode">Действие; <c>null</c> — бросить нельзя.</param>
/// <param name="Refusal">Причина отказа.</param>
public sealed record DropDecision(DropMode? Mode, DropRefusal Refusal)
{
    /// <summary>Отказ по причине.</summary>
    public static DropDecision Refuse(DropRefusal refusal) => new(null, refusal);
}

/// <summary>
/// Окно Drag&amp;Drop у значка в трее (PLAN.md, этап 18, ADR-0011). Область «Бросить в накопитель» (18.2): брошенные файлы
/// падают в основную папку-накопитель — как в Проводнике, на том же диске переносятся, с другого копируются; <c>Shift</c> —
/// перенести, <c>Ctrl</c> — скопировать, и то, что разрешает источник перетаскивания. Итог с «Отменить» показывается в окне,
/// файлы появляются на странице «Разбор». При первом показе после обновления — подсказка о новом поведении значка.
/// </summary>
public sealed partial class DropWindowViewModel : ObservableObject
{
    private readonly HotkeyStatus _hotkeys;
    private readonly ITipsService _tips;
    private readonly ISortingSettings _sorting;
    private readonly IOperationsService _operations;
    private readonly IDispatcher _dispatcher;
    private readonly IAppCommands? _commands;
    private Guid? _operationId;
    private string _resultTarget = string.Empty;
    private string _resultItems = string.Empty;

    /// <summary>
    /// Создаёт модель окна.
    /// </summary>
    public DropWindowViewModel(
        HotkeyStatus hotkeys,
        ITipsService tips,
        ISortingSettings sorting,
        IOperationsService operations,
        IDispatcher dispatcher,
        IAppCommands? commands = null)
    {
        ArgumentNullException.ThrowIfNull(hotkeys);
        ArgumentNullException.ThrowIfNull(tips);
        ArgumentNullException.ThrowIfNull(sorting);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _hotkeys = hotkeys;
        _tips = tips;
        _sorting = sorting;
        _operations = operations;
        _dispatcher = dispatcher;
        _commands = commands;
        _operations.Updated += (_, info) => _dispatcher.Post(() => OnOperationUpdated(info));
        ResetZone();
    }

    /// <summary>Окно просит закрыть его: кнопка, <c>Esc</c> или переход на «Разбор».</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Бросок завершён успешно: окно может спрятаться само через несколько секунд.</summary>
    public event EventHandler? Settled;

    /// <summary>Подсказка под областью; пусто — не показывается.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHint))]
    public partial string Hint { get; set; } = string.Empty;

    /// <summary>Подсказка показана.</summary>
    public bool HasHint => Hint.Length > 0;

    /// <summary>Основная папка-накопитель; <c>null</c> — накопителей нет.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInbox), nameof(HasNoInbox))]
    public partial SortingFolderInfo? Inbox { get; set; }

    /// <summary>Накопитель есть: в область можно бросать.</summary>
    public bool HasInbox => Inbox is not null;

    /// <summary>Накопителя нет: область предлагает выбрать его на странице «Разбор».</summary>
    public bool HasNoInbox => Inbox is null;

    /// <summary>Заголовок области: приглашение или действие при перетаскивании («Переместить в „Загрузки“»).</summary>
    [ObservableProperty]
    public partial string ZoneTitle { get; set; } = string.Empty;

    /// <summary>Пояснение под заголовком.</summary>
    [ObservableProperty]
    public partial string ZoneText { get; set; } = string.Empty;

    /// <summary>Над областью файлы, которые можно бросить: область подсвечена.</summary>
    [ObservableProperty]
    public partial bool IsDragOver { get; set; }

    /// <summary>Итог броска: «3 файла → Загрузки», ход или ошибка; пусто — броска не было.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    public partial string Result { get; set; } = string.Empty;

    /// <summary>Итог показан.</summary>
    public bool HasResult => Result.Length > 0;

    /// <summary>Итог — ошибка.</summary>
    [ObservableProperty]
    public partial bool IsError { get; set; }

    /// <summary>Бросок можно отменить.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    public partial bool CanUndo { get; set; }

    /// <summary>Идёт отправка броска на сервер.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>
    /// Что сделать с брошенными файлами — как Проводник: на том же диске перенос, с другого — копия; <c>Shift</c> —
    /// перенести, <c>Ctrl</c> — скопировать; затем — то, что разрешает источник перетаскивания (мессенджер или браузер
    /// часто разрешает только копию). Источник, не разрешающий ни того ни другого, считается разрешающим копию.
    /// </summary>
    /// <param name="sources">Полные пути брошенных файлов и папок.</param>
    /// <param name="destination">Папка-накопитель.</param>
    /// <param name="allowed">Действия, которые разрешает источник.</param>
    /// <param name="modifiers">Нажатые модификаторы.</param>
    public static DropDecision Decide(IReadOnlyList<string> sources, string? destination, DragDropEffects allowed, KeyModifiers modifiers)
    {
        ArgumentNullException.ThrowIfNull(sources);

        if (destination is null)
        {
            return DropDecision.Refuse(DropRefusal.NoInbox);
        }

        if (sources.Count == 0)
        {
            return DropDecision.Refuse(DropRefusal.NotAllowed);
        }

        var target = Trim(destination);
        if (sources.Any(source => IsSameOrInside(target, Trim(source))))
        {
            return DropDecision.Refuse(DropRefusal.IntoItself);
        }

        if (sources.All(source => Path.GetDirectoryName(Trim(source)) is { } parent && SamePath(Trim(parent), target)))
        {
            return DropDecision.Refuse(DropRefusal.AlreadyThere);
        }

        var ctrl = modifiers.HasFlag(KeyModifiers.Control);
        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        var wanted = ctrl && !shift ? DropMode.Copy
            : shift && !ctrl ? DropMode.Move
            : sources.All(source => SameVolume(source, target)) ? DropMode.Move : DropMode.Copy;

        var canMove = allowed.HasFlag(DragDropEffects.Move);
        var canCopy = allowed.HasFlag(DragDropEffects.Copy) || !canMove;
        return wanted switch
        {
            DropMode.Move when canMove => new DropDecision(DropMode.Move, DropRefusal.None),
            DropMode.Copy when canCopy => new DropDecision(DropMode.Copy, DropRefusal.None),
            _ when canCopy => new DropDecision(DropMode.Copy, DropRefusal.None),
            _ => new DropDecision(DropMode.Move, DropRefusal.None),
        };
    }

    /// <summary>
    /// Окно показано: основная папка-накопитель перечитывается (её могли сменить), при первом показе — подсказка.
    /// </summary>
    public void OnShown()
    {
        if (!HasHint && _tips.ShouldShow(Tips.TrayClicks))
        {
            // Неразрывные пробелы: сочетание не разрывается переносом строки («Ctrl / + Alt + F»).
            Hint = _hotkeys.IsRegistered
                ? L.F("Drop.TrayHint", _hotkeys.QuickSearchText.Replace(' ', ' '))
                : L.T("Drop.TrayHintNoHotkey");
        }

        _ = RefreshAsync();
    }

    /// <summary>
    /// Окно спрятано: показанная подсказка больше не появится, итог прошлого броска убирается.
    /// </summary>
    public void OnHidden()
    {
        if (HasHint)
        {
            _tips.MarkShown(Tips.TrayClicks);
            Hint = string.Empty;
        }

        EndPreview();
        if (!IsBusy)
        {
            ClearResult();
        }
    }

    /// <summary>Перечитывает основную папку-накопитель.</summary>
    public async Task RefreshAsync()
    {
        try
        {
            Inbox = (await _sorting.GetAsync()).Main;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Прежняя папка остаётся: окно открывается и тогда, когда сервер ещё запускается.
            _ = exception;
        }

        ResetZone();
    }

    /// <summary>
    /// Файлы над областью: заголовок показывает, что с ними будет.
    /// </summary>
    /// <returns>Решение о броске.</returns>
    public DropDecision PreviewDrop(IReadOnlyList<string> sources, DragDropEffects allowed, KeyModifiers modifiers)
    {
        var decision = Decide(sources, Inbox?.Path, allowed, modifiers);
        IsDragOver = decision.Mode is not null;
        var name = Inbox?.Title ?? string.Empty;
        (ZoneTitle, ZoneText) = decision switch
        {
            { Mode: DropMode.Move } => (L.F("Drop.MoveTo", name), L.T("Drop.ModifiersHint")),
            { Mode: DropMode.Copy } => (L.F("Drop.CopyTo", name), L.T("Drop.ModifiersHint")),
            { Refusal: DropRefusal.AlreadyThere } => (L.F("Drop.AlreadyThere", name), string.Empty),
            { Refusal: DropRefusal.IntoItself } => (L.T("Drop.IntoItself"), string.Empty),
            { Refusal: DropRefusal.NoInbox } => (L.T("Drop.NoInboxTitle"), L.T("Drop.NoInboxText")),
            _ => (L.T("Drop.NotAllowed"), string.Empty),
        };
        return decision;
    }

    /// <summary>Файлы ушли с области или перетаскивание отменено.</summary>
    public void EndPreview()
    {
        IsDragOver = false;
        ResetZone();
    }

    /// <summary>
    /// Файлы брошены: перенос или копия в основную папку-накопитель одной операцией очереди.
    /// </summary>
    public async Task DropAsync(IReadOnlyList<string> sources, DragDropEffects allowed, KeyModifiers modifiers)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var decision = Decide(sources, Inbox?.Path, allowed, modifiers);
        EndPreview();
        if (decision.Mode is not { } mode || Inbox is not { } inbox || IsBusy)
        {
            return;
        }

        IsBusy = true;
        IsError = false;
        CanUndo = false;
        _resultTarget = inbox.Title;
        _resultItems = Describe(sources);
        Result = L.F(mode == DropMode.Move ? "Drop.Moving" : "Drop.Copying", _resultItems, _resultTarget);
        try
        {
            var operation = await _operations.DropAsync(new DropOperation(sources, inbox.Path, mode == DropMode.Copy));
            _operationId = operation.Id;
            OnOperationUpdated(operation);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ShowError(exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Отменить бросок: перенесённое возвращается, скопированное уходит в корзину.</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        if (_operationId is not { } id)
        {
            return;
        }

        CanUndo = false;
        try
        {
            await _operations.UndoAsync(id);
            _operationId = null;
            IsError = false;
            Result = L.T("Drop.Undone");
            Settled?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ShowError(exception.Message);
        }
    }

    /// <summary>Открыть страницу «Разбор»: там лежат брошенные файлы и добавляются накопители.</summary>
    [RelayCommand]
    private void OpenSorting()
    {
        _commands?.NavigateTo(PageKeys.Inbox);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Закрыть окно.</summary>
    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private static string Trim(string path) => path.TrimEnd('\\', '/');

    private static bool SamePath(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    // Накопитель — сам источник или лежит внутри брошенной папки.
    private static bool IsSameOrInside(string path, string folder)
        => SamePath(path, folder) || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    // Том — буква диска или общая папка сети (\\server\share): так решает и Проводник.
    private static bool SameVolume(string left, string right)
        => Path.GetPathRoot(left) is { Length: > 0 } a && Path.GetPathRoot(right) is { Length: > 0 } b && SamePath(Trim(a), Trim(b));

    private static string Describe(IReadOnlyList<string> sources)
    {
        var folders = sources.Count(Directory.Exists);
        var files = sources.Count - folders;
        var parts = new List<string>();
        if (files > 0)
        {
            parts.Add(Formatting.Count(files, L.T("Common.File.One"), L.T("Common.File.Few"), L.T("Common.File.Many")));
        }

        if (folders > 0)
        {
            parts.Add(Formatting.Count(folders, L.T("Common.Folder.One"), L.T("Common.Folder.Few"), L.T("Common.Folder.Many")));
        }

        return string.Join(L.T("AddFiles.And"), parts);
    }

    private void OnOperationUpdated(OperationInfo info)
    {
        if (info.Id != _operationId)
        {
            return;
        }

        switch (info.State)
        {
            case FileOperationState.Completed:
                IsError = false;
                Result = L.F("Drop.Done", _resultItems, _resultTarget);
                CanUndo = info.CanUndo;
                Settled?.Invoke(this, EventArgs.Empty);
                break;
            case FileOperationState.Failed:
                ShowError(info.Message ?? string.Empty);
                CanUndo = info.CanUndo;
                break;
            case FileOperationState.Cancelled:
                IsError = false;
                Result = L.T("Drop.Cancelled");
                CanUndo = false;
                break;
        }
    }

    private void ShowError(string message)
    {
        IsError = true;
        Result = L.F("Drop.Failed", message);
    }

    private void ClearResult()
    {
        _operationId = null;
        Result = string.Empty;
        IsError = false;
        CanUndo = false;
    }

    private void ResetZone()
    {
        if (Inbox is { } inbox)
        {
            ZoneTitle = L.T("Drop.InboxTitle");
            ZoneText = L.F("Drop.InboxText", inbox.Title);
        }
        else
        {
            ZoneTitle = L.T("Drop.NoInboxTitle");
            ZoneText = L.T("Drop.NoInboxText");
        }
    }
}

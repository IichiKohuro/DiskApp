using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DiskApp.UI.Input;
using DiskApp.UI.Localization;
using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.Text;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.ViewModels;

/// <summary>Действие, ждущее ввода или подтверждения в панели под списком.</summary>
public enum PendingAction
{
    /// <summary>Ничего не ждёт.</summary>
    None,

    /// <summary>Новое имя файла.</summary>
    Rename,

    /// <summary>Подтверждение перемещения в корзину.</summary>
    ConfirmDelete,
}

/// <summary>
/// Действия над выбранным файлом (PLAN.md, этапы 7.2, 7.3 и 7.7): команды клавиш и контекстного меню, панель
/// переименования и подтверждения корзины, уведомления об итоге, история запросов по <c>↑</c>.
/// </summary>
public sealed partial class QuickSearchViewModel
{
    private const int PageStep = 10;

    private static readonly TimeSpan NoticeLifetime = TimeSpan.FromSeconds(4);

    private IReadOnlyList<string>? _history;
    private int _historyIndex = -1;
    private bool _applyingHistory;
    private int _noticeVersion;
    private FileEntry? _pendingEntry;

    /// <summary>
    /// Действие увело пользователя в другое окно (Проводник, свойства, «Открыть с помощью»): окно поиска можно скрыть.
    /// </summary>
    public event EventHandler? DismissRequested;

    /// <summary>Действие, ждущее ввода в панели.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingAction), nameof(IsRenaming), nameof(IsConfirmingDelete), nameof(PendingConfirmText))]
    public partial PendingAction PendingAction { get; set; }

    /// <summary>Подпись панели действия: «Новое имя для …», «Переместить … в корзину?».</summary>
    [ObservableProperty]
    public partial string PendingPrompt { get; set; } = string.Empty;

    /// <summary>Текст поля нового имени.</summary>
    [ObservableProperty]
    public partial string RenameText { get; set; } = string.Empty;

    /// <summary>Сколько символов имени выделить при начале переименования (имя без расширения).</summary>
    public int RenameSelectionLength { get; private set; }

    /// <summary>Уведомление открыто.</summary>
    [ObservableProperty]
    public partial bool IsNoticeOpen { get; set; }

    /// <summary>Заголовок уведомления.</summary>
    [ObservableProperty]
    public partial string NoticeTitle { get; set; } = string.Empty;

    /// <summary>Текст уведомления.</summary>
    [ObservableProperty]
    public partial string NoticeMessage { get; set; } = string.Empty;

    /// <summary>Вид уведомления: успех, предупреждение, ошибка.</summary>
    [ObservableProperty]
    public partial FAInfoBarSeverity NoticeSeverity { get; set; }

    /// <summary>Панель действия открыта.</summary>
    public bool HasPendingAction => PendingAction != PendingAction.None;

    /// <summary>Идёт переименование.</summary>
    public bool IsRenaming => PendingAction == PendingAction.Rename;

    /// <summary>Ждёт подтверждения корзина.</summary>
    public bool IsConfirmingDelete => PendingAction == PendingAction.ConfirmDelete;

    /// <summary>Подпись кнопки подтверждения в панели действия.</summary>
    public string PendingConfirmText => IsRenaming ? L.T("Common.Rename") : L.T("Common.ToRecycleBin");

    /// <summary>Подпись пункта меню закрепления для выбранного файла.</summary>
    public string PinMenuText => SelectedResult?.IsFavorite == true ? L.T("QuickSearch.Unpin") : L.T("QuickSearch.Pin");

    /// <summary>
    /// Выполняет команду клавиши или меню над выбранной строкой.
    /// </summary>
    /// <param name="command">Команда.</param>
    /// <param name="number">Номер строки для <see cref="QuickSearchCommand.OpenNumber"/>.</param>
    public async Task ExecuteAsync(QuickSearchCommand command, int number = 0)
    {
        switch (command)
        {
            case QuickSearchCommand.MoveDown:
                MoveSelection(1);
                break;
            case QuickSearchCommand.MoveUp:
                await MoveUpOrHistoryAsync();
                break;
            case QuickSearchCommand.PageDown:
                MoveSelection(PageStep);
                break;
            case QuickSearchCommand.PageUp:
                MoveSelection(-PageStep);
                break;
            case QuickSearchCommand.First:
                MoveSelection(-Results.Count);
                break;
            case QuickSearchCommand.Last:
                MoveSelection(Results.Count);
                break;
            case QuickSearchCommand.Open:
                await OpenSelectedAsync();
                break;
            case QuickSearchCommand.OpenNumber:
                if (SelectNumber(number))
                {
                    await OpenSelectedAsync();
                }

                break;
            case QuickSearchCommand.Reveal:
                await RevealAsync();
                break;
            case QuickSearchCommand.OpenWith:
                await OpenWithAsync();
                break;
            case QuickSearchCommand.Properties:
                await PropertiesAsync();
                break;
            case QuickSearchCommand.RunAsAdmin:
                await RunAsAdminAsync();
                break;
            case QuickSearchCommand.CopyFiles:
                await CopyFilesAsync();
                break;
            case QuickSearchCommand.CopyPath:
                await CopyPathAsync();
                break;
            case QuickSearchCommand.Rename:
                BeginRename();
                break;
            case QuickSearchCommand.Delete:
                BeginDelete();
                break;
            case QuickSearchCommand.TogglePin:
                await TogglePinAsync();
                break;
            case QuickSearchCommand.EditTags:
                await EditTagsAsync();
                break;
            case QuickSearchCommand.Escape:
                if (HasPendingAction)
                {
                    CancelPendingAction();
                }
                else
                {
                    Escape();
                }

                break;
            case QuickSearchCommand.AcceptSuggestion:
                if (Suggestions.Count > 0)
                {
                    ApplySuggestion(Suggestions[0]);
                }

                break;
            case QuickSearchCommand.ToggleFilters:
                ToggleFilters();
                break;
            case QuickSearchCommand.ContextMenu:
                // Меню показывает представление: ему нужен элемент списка.
                break;
        }
    }

    /// <summary>Открыть выбранный файл (пункт меню).</summary>
    [RelayCommand]
    private async Task OpenAsync() => await OpenSelectedAsync();

    /// <summary>Показать выбранный файл в Проводнике.</summary>
    [RelayCommand]
    private Task RevealAsync() => RunShellActionAsync(static (actions, entry) => actions.RevealAsync(entry), L.T("QuickSearch.CouldNotShowInFolder"));

    /// <summary>«Открыть с помощью» для выбранного файла.</summary>
    [RelayCommand]
    private Task OpenWithAsync() => RunShellActionAsync(static (actions, entry) => actions.OpenWithAsync(entry), L.T("QuickSearch.CouldNotOpenTheOpenWith"));

    /// <summary>Свойства выбранного файла.</summary>
    [RelayCommand]
    private Task PropertiesAsync() => RunShellActionAsync(static (actions, entry) => actions.ShowPropertiesAsync(entry), L.T("QuickSearch.CouldNotOpenProperties"));

    /// <summary>Запустить выбранный файл от имени администратора.</summary>
    [RelayCommand]
    private Task RunAsAdminAsync() => RunShellActionAsync(static (actions, entry) => actions.RunAsAdminAsync(entry), L.T("QuickSearch.CouldNotRunAsAdministrator"));

    /// <summary>Скопировать выбранный файл в буфер обмена.</summary>
    [RelayCommand]
    private async Task CopyFilesAsync()
    {
        if (SelectedResult is not { } hit)
        {
            return;
        }

        if (await TryAsync(() => _actions.CopyFilesAsync([hit.Entry]), L.T("QuickSearch.CouldNotCopy")))
        {
            ShowNotice(L.T("QuickSearch.Copied"), L.F("QuickSearch.PasteItInFileExplorerOr", hit.Entry.Name), FAInfoBarSeverity.Success);
        }
    }

    /// <summary>Скопировать путь выбранного файла.</summary>
    [RelayCommand]
    private async Task CopyPathAsync()
    {
        if (SelectedResult is not { } hit)
        {
            return;
        }

        if (await TryAsync(() => _actions.CopyPathsAsync([hit.Entry]), L.T("QuickSearch.CouldNotCopyThePath")))
        {
            ShowNotice(L.T("QuickSearch.PathCopied"), hit.Entry.FullPath, FAInfoBarSeverity.Success);
        }
    }

    /// <summary>Начать переименование выбранного файла: открывает панель с полем имени.</summary>
    [RelayCommand]
    private void BeginRename()
    {
        if (SelectedResult is not { } hit)
        {
            return;
        }

        _pendingEntry = hit.Entry;
        RenameText = hit.Entry.Name;
        RenameSelectionLength = PathText.StemLength(hit.Entry.Name);
        PendingPrompt = L.F("QuickSearch.NewNameFor", hit.Entry.Name);
        PendingAction = PendingAction.Rename;
    }

    /// <summary>Начать перемещение в корзину: просит подтверждения в панели.</summary>
    [RelayCommand]
    private void BeginDelete()
    {
        if (SelectedResult is not { } hit)
        {
            return;
        }

        _pendingEntry = hit.Entry;
        PendingPrompt = L.F("QuickSearch.MoveToTheRecycleBin", hit.Entry.Name);
        PendingAction = PendingAction.ConfirmDelete;
    }

    /// <summary>Закрепить выбранный файл или открепить его.</summary>
    [RelayCommand]
    private async Task TogglePinAsync()
    {
        if (SelectedResult is not { } hit)
        {
            return;
        }

        var favorite = !hit.Entry.IsFavorite;
        FileEntry? updated = null;
        if (await TryAsync(async () => updated = await _actions.SetFavoriteAsync(hit.Entry, favorite), favorite ? L.T("QuickSearch.CouldNotPin") : L.T("QuickSearch.CouldNotUnpin")))
        {
            ReplaceEntry(hit, updated!);
            ShowNotice(favorite ? L.T("QuickSearch.Pinned") : L.T("QuickSearch.Unpinned"), $"«{hit.Entry.Name}»", FAInfoBarSeverity.Success);
        }
    }

    /// <summary>Теги можно менять: диалог тегов подключён.</summary>
    public bool CanEditTags => _tagEditor is not null;

    /// <summary>Диалог тегов для выбранного файла (PLAN.md, этап 8.7).</summary>
    [RelayCommand]
    private async Task EditTagsAsync()
    {
        if (SelectedResult is not { } hit || _tagEditor is null)
        {
            return;
        }

        IReadOnlyList<FileEntry>? updated = null;
        if (await TryAsync(async () => updated = await _tagEditor.EditAsync([hit.Entry]), L.T("QuickSearch.CouldNotChangeTags")) && updated is { Count: > 0 })
        {
            ReplaceEntry(hit, updated[0]);
            ShowNotice(L.T("QuickSearch.TagsSaved"), $"«{hit.Entry.Name}»", FAInfoBarSeverity.Success);
        }
    }

    /// <summary>
    /// Подтверждает действие в панели: <c>Enter</c> или кнопка.
    /// </summary>
    [RelayCommand]
    public async Task ConfirmPendingActionAsync()
    {
        if (_pendingEntry is not { } entry)
        {
            CancelPendingAction();
            return;
        }

        switch (PendingAction)
        {
            case PendingAction.Rename:
                await RenameAsync(entry, RenameText);
                break;
            case PendingAction.ConfirmDelete:
                CancelPendingAction();
                await DeleteAsync(entry);
                break;
        }
    }

    /// <summary>
    /// Закрывает панель действия без изменений: <c>Esc</c>, кнопка «Отмена» или ввод в поле запроса.
    /// </summary>
    [RelayCommand]
    public void CancelPendingAction()
    {
        _pendingEntry = null;
        PendingAction = PendingAction.None;
    }

    /// <summary>Закрывает уведомление.</summary>
    public void CloseNotice() => IsNoticeOpen = false;

    /// <summary>
    /// Показывает ошибку действия, начатого представлением (перетаскивание).
    /// </summary>
    public void ReportError(string title, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ShowNotice(title, exception.Message, FAInfoBarSeverity.Error);
    }

    private async Task RenameAsync(FileEntry entry, string newName)
    {
        if (PathText.ValidateFileName(newName.Trim()) is { } error)
        {
            ShowNotice(L.T("QuickSearch.ThisNameWillNotWork"), error, FAInfoBarSeverity.Warning);
            return;
        }

        FileEntry? renamed = null;
        if (await TryAsync(async () => renamed = await _actions.RenameAsync(entry, newName), L.T("QuickSearch.CouldNotRename")))
        {
            CancelPendingAction();
            if (FindHit(entry) is { } hit)
            {
                ReplaceEntry(hit, renamed!);
            }

            ShowNotice(L.T("QuickSearch.Renamed"), $"«{entry.Name}» → «{renamed!.Name}»", FAInfoBarSeverity.Success);
        }
    }

    private async Task DeleteAsync(FileEntry entry)
    {
        if (await TryAsync(() => _actions.MoveToRecycleBinAsync([entry]), L.T("QuickSearch.CouldNotMoveToTheRecycle")))
        {
            if (FindHit(entry) is { } hit)
            {
                RemoveHit(hit);
            }

            ShowNotice(L.T("QuickSearch.MovedToTheRecycleBin"), L.F("QuickSearch.CanBeRestoredFromTheWindows", entry.Name), FAInfoBarSeverity.Success);
        }
    }

    private async Task RunShellActionAsync(Func<IFileActions, FileEntry, Task> action, string failureTitle)
    {
        if (SelectedResult is not { } hit)
        {
            return;
        }

        if (await TryAsync(() => action(_actions, hit.Entry), failureTitle))
        {
            DismissRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<bool> TryAsync(Func<Task> action, string failureTitle)
    {
        try
        {
            await action();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception)
        {
            ShowNotice(failureTitle, exception.Message, FAInfoBarSeverity.Error);
            return false;
        }
    }

    private void ShowNotice(string title, string message, FAInfoBarSeverity severity)
    {
        var version = ++_noticeVersion;
        NoticeTitle = title;
        NoticeMessage = message;
        NoticeSeverity = severity;
        IsNoticeOpen = true;

        // Успех закрывается сам; ошибка висит, пока её не закроют или не придёт новое уведомление.
        if (severity == FAInfoBarSeverity.Success)
        {
            _ = CloseNoticeLaterAsync(version);
        }
    }

    private async Task CloseNoticeLaterAsync(int version)
    {
        await Task.Delay(NoticeLifetime);
        if (version == _noticeVersion)
        {
            IsNoticeOpen = false;
        }
    }

    private async Task MoveUpOrHistoryAsync()
    {
        var atTop = SelectedIndex <= 0;
        if (!atTop || (Query.Length > 0 && _historyIndex < 0))
        {
            MoveSelection(-1);
            return;
        }

        _history ??= await _search.GetHistoryAsync(CancellationToken.None);
        if (_historyIndex + 1 >= _history.Count)
        {
            return;
        }

        _historyIndex++;
        _applyingHistory = true;
        try
        {
            Query = _history[_historyIndex];
        }
        finally
        {
            _applyingHistory = false;
        }
    }

    private SearchHit? FindHit(FileEntry entry)
        => Results.FirstOrDefault(hit => string.Equals(hit.Entry.FullPath, entry.FullPath, StringComparison.OrdinalIgnoreCase));

    private void ReplaceEntry(SearchHit hit, FileEntry entry)
    {
        var index = Results.IndexOf(hit);
        var row = Rows.IndexOf(hit);
        if (index < 0 || row < 0)
        {
            return;
        }

        var selected = SelectedIndex;
        var replacement = new SearchHit(entry, hit.Highlight, hit.Number, _clock())
        {
            ShowPath = hit.ShowPath,
            ShowSize = hit.ShowSize,
            ShowDate = hit.ShowDate,
        };
        Results[index] = replacement;
        Rows[row] = replacement;
        SelectedIndex = selected;
        NotifySelectionChanged();
    }

    private void RemoveHit(SearchHit hit)
    {
        var index = Results.IndexOf(hit);
        if (index < 0)
        {
            return;
        }

        Results.RemoveAt(index);
        Rows.Remove(hit);
        ShowResults = Results.Count > 0;
        SelectedIndex = Results.Count == 0 ? -1 : Math.Min(index, Results.Count - 1);
        NotifySelectionChanged();
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedResult));
        OnPropertyChanged(nameof(SelectedRow));
        OnPropertyChanged(nameof(PinMenuText));
        OnPropertyChanged(nameof(SelectedDetails));
    }

    private void OnQueryEdited()
    {
        if (!_applyingHistory)
        {
            _historyIndex = -1;
            _history = null;
        }

        if (IsConfirmingDelete)
        {
            CancelPendingAction();
        }
    }
}

using CommunityToolkit.Mvvm.ComponentModel;

using DiskApp.UI.Models;

namespace DiskApp.UI.ViewModels;

/// <summary>
/// Режим выбора папки для «Сохранить» (PLAN.md, этап 8.6, Quick Switch): окно поиска открывается с фильтром папок,
/// <c>Enter</c> не открывает выбранное, а передаёт папку (для файла — его папку) тому, кто попросил, и закрывает окно.
/// </summary>
public sealed partial class QuickSearchViewModel
{
    private Func<string, Task>? _onFolderPicked;

    /// <summary>Идёт выбор папки для диалога сохранения.</summary>
    [ObservableProperty]
    public partial bool IsPickingFolder { get; set; }

    /// <summary>
    /// Начинает выбор папки: запрос — фильтр папок, <c>Enter</c> отдаёт путь в <paramref name="onPicked"/>.
    /// </summary>
    public void BeginFolderPick(Func<string, Task> onPicked)
    {
        ArgumentNullException.ThrowIfNull(onPicked);
        _onFolderPicked = onPicked;
        IsPickingFolder = true;
        Query = "type:folder ";
        ShowNotice("Выберите папку", "Enter — перейти в неё в диалоге сохранения; для файла — в его папку. Esc — отмена.", FluentAvalonia.UI.Controls.FAInfoBarSeverity.Informational);
    }

    /// <summary>
    /// Сообщает, что выбрать папку для диалога не вышло (диалога нет, он закрылся, не принял путь).
    /// </summary>
    public void ReportFolderPickProblem(string title, string message)
        => ShowNotice(title, message, FluentAvalonia.UI.Controls.FAInfoBarSeverity.Warning);

    /// <summary>Выходит из режима выбора папки без выбора.</summary>
    public void CancelFolderPick()
    {
        _onFolderPicked = null;
        IsPickingFolder = false;
        CloseNotice();
    }

    /// <summary>
    /// В режиме выбора папки: передаёт папку выбранной строки и закрывает окно.
    /// </summary>
    /// <returns><c>true</c>, если режим выбора был и строка выбрана.</returns>
    private async Task<bool> TryPickFolderAsync()
    {
        if (!IsPickingFolder || _onFolderPicked is not { } onPicked || SelectedResult is not { } hit)
        {
            return false;
        }

        var folder = hit.Entry.Kind == FileKind.Folder ? hit.Entry.FullPath : hit.Entry.Directory;
        CancelFolderPick();
        DismissRequested?.Invoke(this, EventArgs.Empty);
        await onPicked(folder);
        return true;
    }
}

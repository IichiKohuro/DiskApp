using Avalonia.Input;

using CommunityToolkit.Mvvm.ComponentModel;

using DiskApp.UI.Localization;
using DiskApp.UI.Models;
using DiskApp.UI.Text;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.ViewModels;

/// <summary>
/// Перетаскивание (PLAN.md, этап 7.6): из результатов во внешние приложения — копированием, с <c>Shift</c> —
/// перемещением; файлы извне — на папку в результатах, они копируются туда. Сценарий «Добавить» с выбором склада —
/// этап 8.5.
/// </summary>
public sealed partial class QuickSearchViewModel
{
    /// <summary>Над окном тащат файлы: показать, куда их бросить.</summary>
    [ObservableProperty]
    public partial bool ShowDropHint { get; set; }

    /// <summary>
    /// Что разрешить при перетаскивании из результатов: копирование, с <c>Shift</c> — перемещение, с <c>Alt</c> — ярлык.
    /// По умолчанию только копирование: Проводник на том же диске иначе переместил бы файл из его места.
    /// </summary>
    public static DragDropEffects DragEffectsFor(KeyModifiers modifiers)
        => modifiers.HasFlag(KeyModifiers.Shift) ? DragDropEffects.Move
            : modifiers.HasFlag(KeyModifiers.Alt) ? DragDropEffects.Link
            : DragDropEffects.Copy;

    /// <summary>
    /// Можно ли бросить файлы на строку: только на доступную папку.
    /// </summary>
    public static bool CanDropInto(SearchHit? hit) => hit is { Entry: { Kind: FileKind.Folder, IsOffline: false } };

    /// <summary>
    /// Копирует брошенные файлы в папку из результатов и сообщает итог; папки пока пропускаются.
    /// </summary>
    /// <param name="folder">Строка-папка, на которую бросили.</param>
    /// <param name="files">Локальные пути брошенных файлов.</param>
    /// <param name="skippedFolders">Сколько брошенных папок пропущено.</param>
    public async Task CopyIntoAsync(SearchHit folder, IReadOnlyList<string> files, int skippedFolders)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(files);

        ShowDropHint = false;
        if (!CanDropInto(folder))
        {
            return;
        }

        var foldersNote = skippedFolders > 0
            ? L.F("QuickSearch.FoldersNotCopiedByDrag", Formatting.Count(skippedFolders, L.T("QuickSearch.FolderSkipped.One"), L.T("QuickSearch.FolderSkipped.Few"), L.T("QuickSearch.FolderSkipped.Many")))
            : string.Empty;
        if (files.Count == 0)
        {
            ShowNotice(L.T("QuickSearch.NothingToCopy"), L.T("QuickSearch.OnlyFilesAreCopiedByDrag") + foldersNote, FAInfoBarSeverity.Warning);
            return;
        }

        var saved = 0;
        if (await TryAsync(async () => saved = await _actions.CopyIntoAsync(files, folder.Entry), L.F("QuickSearch.CouldNotCopyTo", folder.Entry.Name)))
        {
            ShowNotice(
                L.T("QuickSearch.Copied"),
                L.F("Common.CountTo", Formatting.Count(saved, L.T("Common.File.One"), L.T("Common.File.Few"), L.T("Common.File.Many")), folder.Entry.Name) + foldersNote,
                skippedFolders > 0 ? FAInfoBarSeverity.Warning : FAInfoBarSeverity.Success);
        }
    }
}

using DiskApp.UI.Models;
using DiskApp.UI.Text;
using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Services;

/// <summary>
/// Назначение тегов файлам из любого списка (PLAN.md, этап 8.7): быстрый поиск, «Обзор», «Коллекции».
/// </summary>
public interface ITagEditor
{
    /// <summary>
    /// Показывает диалог тегов и записывает выбор.
    /// </summary>
    /// <returns>Файлы с новыми тегами; <c>null</c> — диалог отменён.</returns>
    Task<IReadOnlyList<FileEntry>?> EditAsync(IReadOnlyList<FileEntry> entries);

    /// <summary>
    /// Диалог «Авто-теги папки…» (PLAN.md, этап 19): теги, которые получает всё внутри папки.
    /// </summary>
    /// <returns><c>true</c> — авто-теги изменились.</returns>
    Task<bool> EditAutoTagsAsync(string folderPath);
}

/// <summary>
/// Диалог тегов поверх главного окна. «Перейти к папке» у авто-тега закрывает диалог без сохранения и открывает папку в
/// «Обзоре».
/// </summary>
public sealed class TagEditor(ILibraryService library, IDialogService dialogs, IAppCommands? commands = null) : ITagEditor
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<FileEntry>?> EditAsync(IReadOnlyList<FileEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return [];
        }

        var picker = new TagPickerViewModel(entries, library);
        await picker.LoadAsync();
        var saved = await dialogs.ShowTagPickerAsync(picker);
        if (picker.RequestedFolder is { } folder)
        {
            commands?.BrowseFolder(folder);
            return null;
        }

        return saved ? await picker.ApplyAsync() : null;
    }

    /// <inheritdoc />
    public async Task<bool> EditAutoTagsAsync(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        var editor = await AutoTagsEditorViewModel.LoadAsync(library, folderPath);
        if (!await dialogs.ShowAutoTagsAsync(PathText.FileName(folderPath), editor) || !editor.IsChanged)
        {
            return false;
        }

        await editor.SaveAsync(library, folderPath);
        return true;
    }
}

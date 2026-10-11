using DiskApp.UI.Models;
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
}

/// <summary>
/// Диалог тегов поверх главного окна.
/// </summary>
public sealed class TagEditor(ILibraryService library, IDialogService dialogs) : ITagEditor
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
        return await dialogs.ShowTagPickerAsync(picker) ? await picker.ApplyAsync() : null;
    }
}

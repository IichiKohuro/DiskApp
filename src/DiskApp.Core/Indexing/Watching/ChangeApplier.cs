using DiskApp.Core.Domain;
using DiskApp.Core.Events;
using DiskApp.Core.FileSystem;
using DiskApp.Core.Repositories;

namespace DiskApp.Core.Indexing.Watching;

/// <summary>
/// Применяет склеенные изменения к индексу (PLAN.md, этап 2.3). Переименования внутри корня переносят поддерево
/// одним запросом и сохраняют идентификаторы (а с ними статистику и избранное). Остальные пути проверяются на диске:
/// есть — запись создаётся или обновляется, нет — удаляется с поддеревом. Запись появляется, только если её родитель
/// уже в индексе: так файлы внутри скрытых и исключённых папок не просачиваются через события. Новая папка требует
/// сверки своего поддерева: её могли перенести в корень вместе с содержимым, о котором наблюдатель не сообщает.
/// </summary>
public sealed class ChangeApplier(IFileSystemLister lister, IFileIndexRepository entries, IIndexEventBus events, EntryFactory factory)
{
    /// <summary>
    /// Применяет пакет к корню.
    /// </summary>
    /// <param name="root">Корень.</param>
    /// <param name="changes">Пакет.</param>
    /// <param name="matcher">Правила исключений корня.</param>
    /// <param name="state">Состояние корня: номер сканирования и тронутые пути.</param>
    /// <param name="cancellationToken">Отмена.</param>
    public async Task<ApplyResult> ApplyAsync(Root root, CoalescedChanges changes, ExclusionMatcher matcher, RootIndexState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(matcher);
        ArgumentNullException.ThrowIfNull(state);

        var folders = new FolderChangeSet(factory.Paths);
        var result = new ApplyResult();
        foreach (var (oldRelPath, newRelPath) in changes.Renames)
        {
            state.RecordTouched(oldRelPath);
            state.RecordTouched(newRelPath);
            await ApplyRenameAsync(root, oldRelPath, newRelPath, matcher, folders, result, cancellationToken).ConfigureAwait(false);
        }

        await ApplyPathsAsync(root, changes.Paths, matcher, state, folders, result, cancellationToken).ConfigureAwait(false);
        folders.PublishTo(events, root.Id);
        return result;
    }

    private async Task ApplyRenameAsync(
        Root root,
        string oldRelPath,
        string newRelPath,
        ExclusionMatcher matcher,
        FolderChangeSet folders,
        ApplyResult result,
        CancellationToken cancellationToken)
    {
        var oldExcluded = matcher.IsPathExcluded(oldRelPath);
        var newExcluded = matcher.IsPathExcluded(newRelPath);
        if (oldExcluded)
        {
            // Из исключённого в обычное: это появление, его обработает проверка нового пути.
            return;
        }

        if (newExcluded)
        {
            result.Removed += await RemoveAsync(root, oldRelPath, folders, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (await entries.GetByPathAsync(root.Id, oldRelPath, cancellationToken).ConfigureAwait(false) is not { } renamed)
        {
            return;
        }

        var newParent = factory.Parent(newRelPath);
        if (newParent.Length > 0 && await entries.GetByPathAsync(root.Id, newParent, cancellationToken).ConfigureAwait(false) is null)
        {
            // Новый родитель не индексируется (скрытая или исключённая папка): для индекса это удаление.
            result.Removed += await RemoveAsync(root, oldRelPath, folders, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (await entries.GetByPathAsync(root.Id, newRelPath, cancellationToken).ConfigureAwait(false) is not null)
        {
            // Переименование поверх существующей записи: она заменена.
            result.Removed += await entries.DeleteSubtreeAsync(root.Id, newRelPath, cancellationToken).ConfigureAwait(false);
        }

        result.Renamed += await entries.RenameSubtreeAsync(root.Id, oldRelPath, newRelPath, cancellationToken).ConfigureAwait(false);
        folders.Add(factory.Parent(oldRelPath));
        folders.Add(newParent);
        events.Publish(new EntryRenamedEvent(root.Id, factory.Paths.Combine(root.Path, oldRelPath), factory.Paths.Combine(root.Path, newRelPath), renamed.IsDirectory));
    }

    private async Task ApplyPathsAsync(
        Root root,
        IReadOnlyList<string> paths,
        ExclusionMatcher matcher,
        RootIndexState state,
        FolderChangeSet folders,
        ApplyResult result,
        CancellationToken cancellationToken)
    {
        // Родители раньше детей: новая папка должна оказаться в индексе до своих файлов.
        var ordered = paths.OrderBy(factory.Depth).ThenBy(static path => path, StringComparer.Ordinal).ToList();
        var upserts = new List<FileEntry>();
        var upsertedDirectories = new HashSet<string>(factory.Paths.Comparer);
        var newDirectories = new List<string>();
        var parentKnown = new Dictionary<string, bool>(factory.Paths.Comparer) { [string.Empty] = true };

        foreach (var relPath in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.RecordTouched(relPath);

            if (matcher.IsPathExcluded(relPath))
            {
                result.Removed += await RemoveAsync(root, relPath, folders, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var stat = lister.Stat(factory.FullPath(root, relPath));
            if (stat.Error is not null)
            {
                result.Errors++;
                continue;
            }

            if (stat.IsMissing)
            {
                result.Removed += await RemoveAsync(root, relPath, folders, cancellationToken).ConfigureAwait(false);
                parentKnown[relPath] = false;
                continue;
            }

            var item = stat.Item!.Value;
            var depth = factory.Depth(relPath);
            if (matcher.IsExcluded(item.Name, relPath, item.IsDirectory, item.Attributes, item.Size, depth))
            {
                result.Removed += await RemoveAsync(root, relPath, folders, cancellationToken).ConfigureAwait(false);
                parentKnown[relPath] = false;
                continue;
            }

            var parent = factory.Parent(relPath);
            if (!await IsParentIndexedAsync(root, parent, parentKnown, upsertedDirectories, cancellationToken).ConfigureAwait(false))
            {
                result.Skipped++;
                continue;
            }

            var existing = await entries.GetByPathAsync(root.Id, relPath, cancellationToken).ConfigureAwait(false);
            if (existing is not null && existing.IsDirectory != item.IsDirectory)
            {
                result.Removed += await RemoveAsync(root, relPath, folders, cancellationToken).ConfigureAwait(false);
                existing = null;
            }

            if (existing is not null && !EntryFactory.HasChanged(existing, item))
            {
                parentKnown[relPath] = item.IsDirectory;
                continue;
            }

            upserts.Add(factory.Create(root.Id, relPath, item));
            folders.Add(parent);
            if (item.IsDirectory)
            {
                upsertedDirectories.Add(relPath);
                parentKnown[relPath] = true;
                if (existing is null && !IsUnderAny(relPath, newDirectories))
                {
                    newDirectories.Add(relPath);
                }
            }
        }

        if (upserts.Count > 0)
        {
            var written = await entries.UpsertAsync(upserts, state.CurrentScanId, cancellationToken).ConfigureAwait(false);
            result.Written += written;
        }

        result.NewDirectories.AddRange(newDirectories);
    }

    private async Task<bool> IsParentIndexedAsync(
        Root root,
        string parent,
        Dictionary<string, bool> known,
        HashSet<string> upsertedDirectories,
        CancellationToken cancellationToken)
    {
        if (known.TryGetValue(parent, out var cached))
        {
            return cached;
        }

        var indexed = upsertedDirectories.Contains(parent)
            || await entries.GetByPathAsync(root.Id, parent, cancellationToken).ConfigureAwait(false) is { IsDirectory: true };
        known[parent] = indexed;
        return indexed;
    }

    private async Task<int> RemoveAsync(Root root, string relPath, FolderChangeSet folders, CancellationToken cancellationToken)
    {
        var removed = await entries.DeleteSubtreeAsync(root.Id, relPath, cancellationToken).ConfigureAwait(false);
        if (removed > 0)
        {
            folders.Add(factory.Parent(relPath));
        }

        return removed;
    }

    private bool IsUnderAny(string relPath, List<string> ancestors)
    {
        foreach (var ancestor in ancestors)
        {
            if (relPath.StartsWith(ancestor + factory.Paths.Separator, factory.Paths.Comparison))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Итог применения пакета.
/// </summary>
public sealed class ApplyResult
{
    /// <summary>Записано (добавлено и обновлено).</summary>
    public UpsertResult Written { get; set; }

    /// <summary>Удалено записей вместе с поддеревьями.</summary>
    public int Removed { get; set; }

    /// <summary>Перенесено записей переименованиями.</summary>
    public int Renamed { get; set; }

    /// <summary>Пропущено: родитель не в индексе.</summary>
    public int Skipped { get; set; }

    /// <summary>Пути, которые не удалось прочитать.</summary>
    public int Errors { get; set; }

    /// <summary>Новые папки, содержимое которых надо сверить (без вложенных друг в друга).</summary>
    public List<string> NewDirectories { get; } = [];
}

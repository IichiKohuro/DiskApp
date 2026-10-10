using DiskApp.Core.Events;
using DiskApp.Core.Paths;

namespace DiskApp.Core.Indexing;

/// <summary>
/// Накопитель изменений по папкам (PLAN.md, этап 2.2): событие <see cref="EntriesChangedEvent"/> сообщает,
/// содержимое каких папок изменилось, а не каждую запись. Если папок слишком много, событие сворачивается
/// до «изменился весь корень» — клиенту всё равно дешевле перечитать.
/// </summary>
public sealed class FolderChangeSet(PathNormalizer paths)
{
    /// <summary>Сколько папок перечисляется в событии до сворачивания.</summary>
    public const int MaxFolders = 256;

    private readonly HashSet<string> _folders = new(paths.Comparer);
    private bool _wholeRoot;

    /// <summary>Есть ли изменения.</summary>
    public bool HasChanges => _wholeRoot || _folders.Count > 0;

    /// <summary>Отмечает изменение содержимого папки (пустая строка — верхний уровень корня).</summary>
    public void Add(string folderRelPath)
    {
        ArgumentNullException.ThrowIfNull(folderRelPath);

        if (_wholeRoot)
        {
            return;
        }

        _folders.Add(folderRelPath);
        if (_folders.Count > MaxFolders)
        {
            _wholeRoot = true;
            _folders.Clear();
        }
    }

    /// <summary>
    /// Изменения для события и очистка накопителя. Каждая папка — <see cref="EntryChangeKind.Modified"/> с признаком папки.
    /// </summary>
    public IReadOnlyList<EntryChange> Drain()
    {
        IReadOnlyList<EntryChange> changes = _wholeRoot
            ? [new EntryChange(EntryChangeKind.Modified, string.Empty, null, true)]
            : _folders.Order(StringComparer.Ordinal).Select(static folder => new EntryChange(EntryChangeKind.Modified, folder, null, true)).ToArray();

        _wholeRoot = false;
        _folders.Clear();
        return changes;
    }

    /// <summary>
    /// Публикует накопленное событием корня, если есть что публиковать.
    /// </summary>
    public void PublishTo(IIndexEventBus events, long rootId)
    {
        ArgumentNullException.ThrowIfNull(events);

        if (HasChanges)
        {
            events.Publish(new EntriesChangedEvent(rootId, Drain()));
        }
    }
}

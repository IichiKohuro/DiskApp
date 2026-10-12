using System.IO.Abstractions;

using DiskApp.Core.Abstractions;
using DiskApp.Core.Domain;
using DiskApp.Core.Indexing;
using DiskApp.Core.Paths;
using DiskApp.Core.Repositories;
using DiskApp.Core.Settings;

namespace DiskApp.Core.Sorting;

/// <summary>Состояние файла в папке-накопителе (PLAN.md, этап 17.1).</summary>
public enum InboxItemState
{
    /// <summary>Ждёт разбора.</summary>
    New = 0,

    /// <summary>Отложен: виден в конце списка, но не считается в бейдже.</summary>
    Postponed = 1,

    /// <summary>«Оставить здесь»: больше не предлагается к разбору.</summary>
    Kept = 2,

    /// <summary>
    /// Новый, но авторазбор его не трогает (этап 17.5): файл вернулся после автопереноса — отменой или вручную. Для интерфейса
    /// и бейджа — обычный новый.
    /// </summary>
    Manual = 3,
}

/// <summary>Почему накопитель не прочитан.</summary>
public enum InboxProblem
{
    /// <summary>Всё в порядке.</summary>
    None = 0,

    /// <summary>Папка вне добавленных корней — её нет в индексе.</summary>
    OutsideIndex = 1,

    /// <summary>Корень недоступен: диск отключён или пропала сеть.</summary>
    Offline = 2,

    /// <summary>Папки нет: переименована, перенесена или удалена.</summary>
    Missing = 3,
}

/// <summary>Накопитель в очереди.</summary>
/// <param name="Path">Путь из настроек.</param>
/// <param name="RootId">Корень, в который он входит; <c>null</c> — вне корней.</param>
/// <param name="Problem">Почему не прочитан.</param>
public sealed record InboxFolderStatus(string Path, long? RootId, InboxProblem Problem);

/// <summary>Файл или папка в накопителе.</summary>
/// <param name="Entry">Запись индекса.</param>
/// <param name="FullPath">Полный путь.</param>
/// <param name="Inbox">Путь накопителя из настроек.</param>
/// <param name="State">Состояние.</param>
/// <param name="ReadyUtc">С какого момента файл считается пришедшим: время изменения плюс «время успокоения».</param>
public sealed record InboxItem(FileEntry Entry, string FullPath, string Inbox, InboxItemState State, DateTimeOffset ReadyUtc);

/// <summary>Очередь накопителей на момент чтения.</summary>
/// <param name="Folders">Накопители по порядку настроек.</param>
/// <param name="Items">Файлы, новые сверху; ещё не пришедшие тоже — с <see cref="InboxItem.ReadyUtc"/> в будущем.</param>
/// <param name="NowUtc">Момент чтения.</param>
/// <param name="IsTruncated">В каком-то накопителе файлов больше <see cref="InboxService.MaxItemsPerInbox"/> — показаны самые новые.</param>
public sealed record InboxSnapshot(IReadOnlyList<InboxFolderStatus> Folders, IReadOnlyList<InboxItem> Items, DateTimeOffset NowUtc, bool IsTruncated)
{
    /// <summary>Файл уже пришёл: не меняется дольше «времени успокоения».</summary>
    public bool IsReady(InboxItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.ReadyUtc <= NowUtc;
    }

    /// <summary>Сколько файлов ждут разбора: пришедшие и не отложенные.</summary>
    public int NewCount => Items.Count(item => item.State is InboxItemState.New or InboxItemState.Manual && IsReady(item));

    /// <summary>Когда придёт следующий из ещё дописываемых файлов; <c>null</c> — таких нет.</summary>
    public DateTimeOffset? NextReadyUtc => Items.Where(item => !IsReady(item)).Select(static item => (DateTimeOffset?)item.ReadyUtc).Min();
}

/// <summary>
/// Очередь новых файлов в папках-накопителях (PLAN.md, этап 17.1). Файл попадает в неё, когда пришёл — не меняется дольше
/// «времени успокоения», то есть загрузка завершена. Временные, служебные, скрытые и системные файлы в неё не попадают.
/// Подпапки накопителя — по его настройке. Состояния «отложен» и «оставить здесь» хранятся в базе по пути; записи
/// пропавших файлов убираются при чтении. Очередь считается из индекса, поэтому видит и то, что пришло мимо DiskApp.
/// </summary>
public sealed class InboxService(
    ISettingsStore<AppSettings> settings,
    IRootRepository roots,
    IFileIndexRepository entries,
    IInboxStateRepository states,
    IClock clock,
    PathNormalizer paths,
    IFileSystem fileSystem)
{
    /// <summary>Сколько файлов одного накопителя показывать: больше — только самые новые.</summary>
    public const int MaxItemsPerInbox = 5_000;

    /// <summary>Наибольшее число путей в одном изменении состояния.</summary>
    public const int MaxStatePaths = 1_000;

    /// <summary>Читает очередь всех накопителей.</summary>
    public async Task<InboxSnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        var sorting = settings.Current.Sorting;
        var now = clock.UtcNow;
        var settle = TimeSpan.FromSeconds(Math.Clamp(sorting.SettleSeconds, 0, SortingSettings.MaxSettleSeconds));
        var ignoreCase = paths.Comparison == StringComparison.OrdinalIgnoreCase;
        var ignore = sorting.IgnorePatterns.Where(static pattern => !string.IsNullOrWhiteSpace(pattern)).Select(pattern => new GlobPattern(pattern.Trim(), ignoreCase)).ToArray();
        var allRoots = await roots.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var known = await states.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var stateByPath = new Dictionary<string, InboxItemState>(known, paths.Comparer);

        var folders = new List<InboxFolderStatus>(sorting.Inboxes.Count);
        var items = new List<InboxItem>();
        var listedFolders = new List<string>();
        var seen = new HashSet<string>(paths.Comparer);
        var truncated = false;
        foreach (var inbox in sorting.Inboxes)
        {
            var (status, folder, root, relative) = await ResolveAsync(inbox.Path, allRoots, cancellationToken).ConfigureAwait(false);
            folders.Add(status);
            if (status.Problem != InboxProblem.None)
            {
                continue;
            }

            listedFolders.Add(folder!);
            var listed = inbox.IncludeSubfolders
                ? await entries.GetSubtreeFilesAsync(root!.Id, relative!, MaxItemsPerInbox + 1, cancellationToken).ConfigureAwait(false)
                : await entries.GetChildrenOfPathAsync(root!.Id, relative!, cancellationToken).ConfigureAwait(false);
            var visible = listed
                .Where(entry => (entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0 && !ignore.Any(glob => glob.IsMatch(entry.Name, entry.RelPath)))
                .OrderByDescending(static entry => entry.ModifiedUtc)
                .ToArray();
            truncated |= visible.Length > MaxItemsPerInbox;
            foreach (var entry in visible.Take(MaxItemsPerInbox))
            {
                // Накопитель внутри другого накопителя с подпапками: файл показывается один раз — у первого по списку.
                var fullPath = entry.GetFullPath(root.Path, paths);
                if (seen.Add(fullPath))
                {
                    items.Add(new InboxItem(entry, fullPath, inbox.Path, stateByPath.GetValueOrDefault(fullPath), entry.ModifiedUtc + settle));
                }
            }
        }

        await PruneAsync(stateByPath.Keys, seen, listedFolders, cancellationToken).ConfigureAwait(false);
        items.Sort(static (left, right) => right.Entry.ModifiedUtc.CompareTo(left.Entry.ModifiedUtc));
        return new InboxSnapshot(folders, items, now, truncated);
    }

    /// <summary>
    /// Задаёт состояние файлам накопителей: «отложен», «оставить здесь» или снова «новый».
    /// </summary>
    /// <returns><c>false</c> — среди путей есть лежащие вне накопителей; ничего не изменено.</returns>
    public async Task<bool> SetStateAsync(IReadOnlyList<string> fullPaths, InboxItemState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fullPaths);

        var inboxes = settings.Current.Sorting.Inboxes
            .Select(inbox => paths.TryNormalize(inbox.Path, out var normalized) ? normalized : null)
            .OfType<string>()
            .ToArray();
        var normalizedPaths = new List<string>(fullPaths.Count);
        foreach (var path in fullPaths)
        {
            if (!paths.TryNormalize(path, out var normalized) || !inboxes.Any(inbox => paths.IsNestedIn(normalized, inbox)))
            {
                return false;
            }

            normalizedPaths.Add(normalized);
        }

        await states.SetAsync([.. normalizedPaths.Distinct(paths.Comparer)], state, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<(InboxFolderStatus Status, string? Folder, Root? Root, string? Relative)> ResolveAsync(string path, IReadOnlyList<Root> allRoots, CancellationToken cancellationToken)
    {
        if (!paths.TryNormalize(path, out var folder))
        {
            return (new InboxFolderStatus(path, null, InboxProblem.OutsideIndex), null, null, null);
        }

        var root = allRoots.Where(candidate => paths.Comparer.Equals(candidate.Path, folder) || paths.IsNestedIn(folder, candidate.Path)).MaxBy(static candidate => candidate.Path.Length);
        if (root is null || !paths.TryGetRelativePath(root.Path, folder, out var relative))
        {
            return (new InboxFolderStatus(path, null, InboxProblem.OutsideIndex), null, null, null);
        }

        if (root.Status == RootStatus.Offline)
        {
            return (new InboxFolderStatus(path, root.Id, InboxProblem.Offline), null, null, null);
        }

        if (relative.Length > 0 && await entries.GetByPathAsync(root.Id, relative, cancellationToken).ConfigureAwait(false) is not { IsDirectory: true })
        {
            return (new InboxFolderStatus(path, root.Id, InboxProblem.Missing), null, null, null);
        }

        return (new InboxFolderStatus(path, root.Id, InboxProblem.None), folder, root, relative);
    }

    // Записи о файлах, которых больше нет на диске (разобрали, удалили), убираются. Смотрятся только пути внутри прочитанных
    // накопителей: у отключённого диска состояния сохраняются до его возвращения.
    private async Task PruneAsync(IEnumerable<string> storedPaths, HashSet<string> listed, List<string> listedFolders, CancellationToken cancellationToken)
    {
        var gone = storedPaths
            .Where(path => !listed.Contains(path) && listedFolders.Any(folder => paths.IsNestedIn(path, folder)))
            .Where(path => !fileSystem.File.Exists(path) && !fileSystem.Directory.Exists(path))
            .ToArray();
        if (gone.Length > 0)
        {
            await states.RemoveAsync(gone, cancellationToken).ConfigureAwait(false);
        }
    }
}

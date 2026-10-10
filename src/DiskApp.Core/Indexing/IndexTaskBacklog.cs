using DiskApp.Core.Paths;

namespace DiskApp.Core.Indexing;

/// <summary>
/// Задача индексации корня или поддерева.
/// </summary>
/// <param name="RootId">Корень.</param>
/// <param name="Kind">Сверка или полное сканирование.</param>
/// <param name="RelPath">Поддерево; пустая строка — весь корень.</param>
/// <param name="Reason">Зачем: для журнала и диагностики.</param>
public sealed record IndexTask(long RootId, IndexTaskKind Kind, string RelPath, string Reason)
{
    /// <summary>
    /// Приоритет, меньше — важнее (PLAN.md, этап 2.4: изменения от наблюдателя &gt; сверка &gt; полное сканирование):
    /// 0 — сверка поддерева по событию наблюдателя, 1 — сверка корня, 2 — полное сканирование.
    /// </summary>
    public int Priority => Kind switch
    {
        IndexTaskKind.Reconcile when RelPath.Length > 0 => 0,
        IndexTaskKind.Reconcile => 1,
        _ => 2,
    };
}

/// <summary>
/// Очередь задач индексации с приоритетами и поглощением: задача не ставится, если её уже покрывает ожидающая
/// (того же корня, на том же или охватывающем пути, не слабее по виду), а новая задача убирает покрытые ею.
/// Полное сканирование покрывает сверку, весь корень — любое поддерево. Не потокобезопасна: её охраняет владелец.
/// </summary>
public sealed class IndexTaskBacklog(PathNormalizer paths)
{
    private readonly List<(IndexTask Task, long Sequence)> _pending = [];
    private long _sequence;

    /// <summary>Ожидающих задач.</summary>
    public int Count => _pending.Count;

    /// <summary>
    /// Ставит задачу; <c>false</c>, если её уже покрывает ожидающая.
    /// </summary>
    public bool Enqueue(IndexTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        foreach (var (existing, _) in _pending)
        {
            if (Covers(existing, task))
            {
                return false;
            }
        }

        _pending.RemoveAll(item => Covers(task, item.Task));
        _pending.Add((task, _sequence++));
        return true;
    }

    /// <summary>
    /// Забирает самую важную задачу из тех, что можно запустить сейчас (диск и корень свободны); при равном приоритете — более раннюю.
    /// </summary>
    public IndexTask? TryDequeue(Func<IndexTask, bool> canRun)
    {
        ArgumentNullException.ThrowIfNull(canRun);

        var bestIndex = -1;
        for (var i = 0; i < _pending.Count; i++)
        {
            var candidate = _pending[i];
            if (!canRun(candidate.Task))
            {
                continue;
            }

            if (bestIndex < 0
                || candidate.Task.Priority < _pending[bestIndex].Task.Priority
                || (candidate.Task.Priority == _pending[bestIndex].Task.Priority && candidate.Sequence < _pending[bestIndex].Sequence))
            {
                bestIndex = i;
            }
        }

        if (bestIndex < 0)
        {
            return null;
        }

        var task = _pending[bestIndex].Task;
        _pending.RemoveAt(bestIndex);
        return task;
    }

    /// <summary>Есть ли ожидающие задачи корня.</summary>
    public bool HasPendingFor(long rootId) => _pending.Exists(item => item.Task.RootId == rootId);

    /// <summary>Убирает все задачи корня (корень удалён или стал недоступен).</summary>
    public int RemoveRoot(long rootId) => _pending.RemoveAll(item => item.Task.RootId == rootId);

    /// <summary>Ожидающие задачи в порядке выполнения.</summary>
    public IReadOnlyList<IndexTask> Snapshot()
        => _pending.OrderBy(static item => item.Task.Priority).ThenBy(static item => item.Sequence).Select(static item => item.Task).ToArray();

    private bool Covers(IndexTask outer, IndexTask inner)
        => outer.RootId == inner.RootId
            && outer.Kind >= inner.Kind
            && (outer.RelPath.Length == 0
                || paths.Comparer.Equals(outer.RelPath, inner.RelPath)
                || inner.RelPath.StartsWith(outer.RelPath + paths.Separator, paths.Comparison));
}

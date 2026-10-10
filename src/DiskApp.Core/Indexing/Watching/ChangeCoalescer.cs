using DiskApp.Core.Paths;

namespace DiskApp.Core.Indexing.Watching;

/// <summary>
/// Склейка событий наблюдателя (PLAN.md, этап 2.3). Изменения накапливаются, пока идёт поток событий, и отдаются
/// пакетом после паузы <see cref="Quiet"/>, но не позже <see cref="MaxDelay"/> после первого события: так серия
/// «создан, изменён, изменён» превращается в одну проверку пути, а изменение отражается в индексе меньше чем за секунду.
/// Переименования сохраняются в порядке поступления (цепочки A→B→C применяются по очереди и сохраняют идентификатор
/// записи); остальные события сводятся к множеству путей, состояние которых проверяется на диске при применении.
/// Не потокобезопасен: им владеет один цикл обработки.
/// </summary>
public sealed class ChangeCoalescer(PathNormalizer paths, TimeSpan quiet, TimeSpan maxDelay, int maxPaths = ChangeCoalescer.DefaultMaxPaths)
{
    /// <summary>Сколько путей копится до признания пакета переполненным (дешевле сверить корень).</summary>
    public const int DefaultMaxPaths = 10_000;

    private readonly List<(string OldRelPath, string NewRelPath)> _renames = [];
    private readonly HashSet<string> _dirty = new(paths.Comparer);
    private DateTimeOffset? _first;
    private DateTimeOffset _last;
    private bool _overflowed;

    /// <summary>Пауза в событиях, после которой пакет готов.</summary>
    public TimeSpan Quiet { get; } = quiet;

    /// <summary>Наибольшая задержка от первого события пакета.</summary>
    public TimeSpan MaxDelay { get; } = maxDelay;

    /// <summary>Есть накопленные изменения.</summary>
    public bool HasPending => _first is not null;

    /// <summary>Когда пакет будет готов; <c>null</c>, если копить нечего.</summary>
    public DateTimeOffset? DueAt => _first is { } first ? Min(_last + Quiet, first + MaxDelay) : null;

    /// <summary>
    /// Добавляет событие.
    /// </summary>
    public void Add(RawChange change, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(change);

        _first ??= now;
        _last = now;
        if (_overflowed)
        {
            return;
        }

        var relPath = paths.NormalizeRelative(change.RelPath);
        if (relPath.Length == 0)
        {
            return;
        }

        if (change.Kind == RawChangeKind.Renamed && change.OldRelPath is { } old)
        {
            var oldRelPath = paths.NormalizeRelative(old);
            if (oldRelPath.Length > 0)
            {
                _renames.Add((oldRelPath, relPath));
                _dirty.Add(oldRelPath);
            }
        }

        _dirty.Add(relPath);
        if (_dirty.Count > maxPaths)
        {
            MarkOverflowed();
        }
    }

    /// <summary>
    /// Отмечает потерю событий (переполнение буфера ОС): пакет заменяется сверкой корня.
    /// </summary>
    public void MarkOverflowed()
    {
        _overflowed = true;
        _renames.Clear();
        _dirty.Clear();
    }

    /// <summary>Готов ли пакет к моменту <paramref name="now"/>.</summary>
    public bool IsDue(DateTimeOffset now) => DueAt is { } due && now >= due;

    /// <summary>
    /// Забирает накопленное и очищает состояние.
    /// </summary>
    public CoalescedChanges Drain()
    {
        var batch = new CoalescedChanges([.. _renames], [.. _dirty], _overflowed);
        _renames.Clear();
        _dirty.Clear();
        _first = null;
        _overflowed = false;
        return batch;
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;
}

/// <summary>
/// Пакет склеенных изменений.
/// </summary>
/// <param name="Renames">Переименования в порядке поступления.</param>
/// <param name="Paths">Пути, состояние которых надо проверить на диске.</param>
/// <param name="Overflowed">События терялись: вместо применения нужна сверка корня.</param>
public sealed record CoalescedChanges(IReadOnlyList<(string OldRelPath, string NewRelPath)> Renames, IReadOnlyList<string> Paths, bool Overflowed)
{
    /// <summary>Пакет пуст.</summary>
    public bool IsEmpty => !Overflowed && Renames.Count == 0 && Paths.Count == 0;
}

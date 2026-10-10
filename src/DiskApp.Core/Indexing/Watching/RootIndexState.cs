using DiskApp.Core.Paths;

namespace DiskApp.Core.Indexing.Watching;

/// <summary>
/// Общее состояние индексации одного корня для наблюдателя и задач сканирования. Пока идёт полное сканирование,
/// наблюдатель помечает свои записи его номером (иначе sweep удалил бы их) и запоминает тронутые пути, чтобы после
/// сканирования перепроверить их: сканер мог прочитать файл до удаления и вернуть его в индекс.
/// </summary>
public sealed class RootIndexState(PathNormalizer paths)
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _touchedDuringScan = new(paths.Comparer);
    private long? _activeScanId;
    private long _lastScanId;

    /// <summary>Номер сканирования для записей наблюдателя: текущего полного сканирования или последнего известного.</summary>
    public long CurrentScanId
    {
        get
        {
            lock (_gate)
            {
                return _activeScanId ?? _lastScanId;
            }
        }
    }

    /// <summary>Идёт ли сейчас сканирование с пометкой записей.</summary>
    public bool IsScanActive
    {
        get
        {
            lock (_gate)
            {
                return _activeScanId is not null;
            }
        }
    }

    /// <summary>Сканирование началось с этим номером.</summary>
    public void BeginScan(long scanId)
    {
        lock (_gate)
        {
            _activeScanId = scanId;
            _touchedDuringScan.Clear();
        }
    }

    /// <summary>
    /// Сканирование закончилось; возвращает пути, тронутые наблюдателем за время сканирования.
    /// </summary>
    public IReadOnlyList<string> EndScan()
    {
        lock (_gate)
        {
            if (_activeScanId is { } id)
            {
                _lastScanId = Math.Max(_lastScanId, id);
            }

            _activeScanId = null;
            var touched = _touchedDuringScan.ToArray();
            _touchedDuringScan.Clear();
            return touched;
        }
    }

    /// <summary>Запоминает путь, тронутый наблюдателем, если идёт сканирование.</summary>
    public void RecordTouched(string relPath)
    {
        lock (_gate)
        {
            if (_activeScanId is not null)
            {
                _touchedDuringScan.Add(relPath);
            }
        }
    }
}

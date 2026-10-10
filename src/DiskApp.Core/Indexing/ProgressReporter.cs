using System.Diagnostics;

using DiskApp.Core.Events;

namespace DiskApp.Core.Indexing;

/// <summary>
/// Публикует <see cref="IndexProgressEvent"/> не чаще заданного интервала (PLAN.md, этап 2.1: не чаще 4 раз в секунду);
/// смена фазы публикуется всегда. Скорость — записей в секунду с начала задачи.
/// </summary>
internal sealed class ProgressReporter(IIndexEventBus events, long rootId, TimeSpan interval)
{
    /// <summary>Интервал по умолчанию: 250 мс.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(250);

    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly Lock _gate = new();
    private TimeSpan _lastPublished = TimeSpan.MinValue;
    private IndexPhase? _lastPhase;

    /// <summary>Сколько событий опубликовано; для тестов.</summary>
    public int Published { get; private set; }

    /// <summary>
    /// Сообщает о ходе; публикует, если сменилась фаза или прошёл интервал.
    /// </summary>
    public void Report(IndexPhase phase, long scanned, long written, string? currentPath)
    {
        IndexProgressEvent progress;
        lock (_gate)
        {
            var elapsed = _stopwatch.Elapsed;
            if (_lastPhase == phase && elapsed - _lastPublished < interval)
            {
                return;
            }

            _lastPhase = phase;
            _lastPublished = elapsed;
            Published++;
            var rate = elapsed.TotalSeconds > 0 ? Math.Max(scanned, written) / elapsed.TotalSeconds : 0;
            progress = new IndexProgressEvent(rootId, phase, scanned, written, phase == IndexPhase.Done ? null : currentPath, rate, elapsed);
        }

        events.Publish(progress);
    }
}

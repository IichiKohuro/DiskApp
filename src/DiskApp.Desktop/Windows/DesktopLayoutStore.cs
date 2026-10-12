using System.Collections.Concurrent;

using DiskApp.UI.Services;

namespace DiskApp.Desktop.Windows;

/// <summary>
/// Ширина панелей страниц в <c>window-state.json</c>: читается один раз при запуске, пишется через полсекунды после
/// последнего изменения — разделитель, пока его тянут, меняет ширину на каждом шаге.
/// </summary>
internal sealed class DesktopLayoutStore : ILayoutStore, IDisposable
{
    private static readonly TimeSpan WriteDelay = TimeSpan.FromMilliseconds(500);

    private readonly WindowPlacementStore _store;
    private readonly ConcurrentDictionary<string, double> _widths;
    private readonly Timer _timer;
    private int _dirty;

    /// <summary>Создаёт хранилище поверх файла состояния окон.</summary>
    public DesktopLayoutStore(WindowPlacementStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _widths = new ConcurrentDictionary<string, double>(store.LoadPanels(), StringComparer.Ordinal);
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <inheritdoc />
    public double? GetWidth(string key) => _widths.TryGetValue(key, out var width) ? width : null;

    /// <inheritdoc />
    public void SetWidth(string key, double width)
    {
        if (!double.IsFinite(width) || width <= 0)
        {
            return;
        }

        _widths[key] = Math.Round(width);
        Interlocked.Exchange(ref _dirty, 1);
        _timer.Change(WriteDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Записывает несохранённое сразу — при выходе.</summary>
    public void Flush()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 1)
        {
            _store.SavePanels(_widths);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer.Dispose();
        Flush();
    }
}

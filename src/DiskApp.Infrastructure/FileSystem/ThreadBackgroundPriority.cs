using DiskApp.Core.FileSystem;
using DiskApp.Infrastructure.Platform.Windows;

namespace DiskApp.Infrastructure.FileSystem;

/// <summary>
/// Фоновый приоритет потока: <see cref="ThreadPriority.BelowNormal"/> везде и <c>THREAD_MODE_BACKGROUND_BEGIN</c> в Windows,
/// который снижает ещё и приоритет ввода-вывода и памяти. Восстанавливается на том же потоке при освобождении области.
/// </summary>
public sealed class ThreadBackgroundPriority : IBackgroundPriority
{
    /// <summary>Единственный экземпляр.</summary>
    public static ThreadBackgroundPriority Instance { get; } = new();

    private ThreadBackgroundPriority()
    {
    }

    /// <inheritdoc />
    public IDisposable Enter()
    {
        var thread = Thread.CurrentThread;
        var previous = thread.Priority;
        thread.Priority = ThreadPriority.BelowNormal;

        var background = OperatingSystem.IsWindows()
            && NativeMethods.SetThreadPriority(NativeMethods.GetCurrentThread(), NativeMethods.ThreadModeBackgroundBegin);

        return new Scope(thread, previous, background);
    }

    private sealed class Scope(Thread thread, ThreadPriority previous, bool background) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed || Thread.CurrentThread != thread)
            {
                return;
            }

            _disposed = true;
            if (background && OperatingSystem.IsWindows())
            {
                NativeMethods.SetThreadPriority(NativeMethods.GetCurrentThread(), NativeMethods.ThreadModeBackgroundEnd);
            }

            thread.Priority = previous;
        }
    }
}

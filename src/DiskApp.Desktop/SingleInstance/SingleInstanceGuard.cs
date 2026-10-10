namespace DiskApp.Desktop.SingleInstance;

/// <summary>
/// Захват именованного мьютекса: первый экземпляр владеет им до выхода, остальные видят, что место занято.
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;

    private SingleInstanceGuard(Mutex mutex, bool isFirstInstance)
    {
        _mutex = mutex;
        IsFirstInstance = isFirstInstance;
    }

    /// <summary>Этот процесс — первый экземпляр.</summary>
    public bool IsFirstInstance { get; }

    /// <summary>
    /// Пытается стать первым экземпляром с мьютексом <paramref name="mutexName"/>.
    /// Брошенный мьютекс (предыдущий экземпляр упал) считается свободным.
    /// </summary>
    public static SingleInstanceGuard Acquire(string mutexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);

        var mutex = new Mutex(initiallyOwned: false, mutexName);
        bool owned;
        try
        {
            owned = mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            owned = true;
        }

        return new SingleInstanceGuard(mutex, owned);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (IsFirstInstance)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Мьютекс освобождается из другого потока, чем захвачен (выход из приложения): владение всё равно снимется при закрытии дескриптора.
            }
        }

        _mutex.Dispose();
    }
}

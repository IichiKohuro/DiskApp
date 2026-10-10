namespace DiskApp.Core.FileSystem;

/// <summary>
/// Понижение приоритета текущего потока на время фоновой работы (PLAN.md, этап 2.1): сканер не должен мешать
/// пользователю ни процессором, ни диском. В Windows это ещё и низкий приоритет ввода-вывода
/// (<c>THREAD_MODE_BACKGROUND_BEGIN</c>). Вызывать только на выделенном потоке, не на потоке пула.
/// </summary>
public interface IBackgroundPriority
{
    /// <summary>
    /// Понижает приоритет текущего потока; освобождение возвращённого объекта восстанавливает прежний.
    /// </summary>
    IDisposable Enter();
}

/// <summary>
/// Приоритет не меняется: для тестов и платформ без поддержки.
/// </summary>
public sealed class NoBackgroundPriority : IBackgroundPriority
{
    /// <summary>Единственный экземпляр.</summary>
    public static NoBackgroundPriority Instance { get; } = new();

    private NoBackgroundPriority()
    {
    }

    /// <inheritdoc />
    public IDisposable Enter() => Scope.Instance;

    private sealed class Scope : IDisposable
    {
        public static Scope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

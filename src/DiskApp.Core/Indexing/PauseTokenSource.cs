namespace DiskApp.Core.Indexing;

/// <summary>
/// Пауза и возобновление долгих задач индексации (PLAN.md, этап 2.1). В отличие от отмены, пауза обратима:
/// задача доходит до ближайшей точки проверки (между папками) и ждёт <see cref="Resume"/>.
/// </summary>
public sealed class PauseTokenSource
{
    private readonly Lock _gate = new();
    private TaskCompletionSource? _resumed;

    /// <summary>Пауза включена.</summary>
    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _resumed is not null;
            }
        }
    }

    /// <summary>Токен для задач.</summary>
    public PauseToken Token => new(this);

    /// <summary>
    /// Включает паузу; повторный вызов ничего не меняет.
    /// </summary>
    public void Pause()
    {
        lock (_gate)
        {
            _resumed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>
    /// Снимает паузу и отпускает ждущие задачи; повторный вызов ничего не меняет.
    /// </summary>
    public void Resume()
    {
        TaskCompletionSource? resumed;
        lock (_gate)
        {
            resumed = _resumed;
            _resumed = null;
        }

        resumed?.TrySetResult();
    }

    /// <summary>
    /// Задача, которая завершится при снятии паузы; уже завершена, если паузы нет.
    /// </summary>
    internal Task WhenResumed()
    {
        lock (_gate)
        {
            return _resumed?.Task ?? Task.CompletedTask;
        }
    }
}

/// <summary>
/// Сторона задачи: проверяет паузу и ждёт её снятия. <c>default</c> никогда не на паузе.
/// </summary>
public readonly struct PauseToken : IEquatable<PauseToken>
{
    private readonly PauseTokenSource? _source;

    internal PauseToken(PauseTokenSource source)
    {
        _source = source;
    }

    /// <summary>Токен без паузы.</summary>
    public static PauseToken None => default;

    /// <summary>Пауза включена.</summary>
    public bool IsPaused => _source?.IsPaused ?? false;

    /// <summary>
    /// Асинхронно ждёт снятия паузы; сразу завершается, если паузы нет.
    /// </summary>
    public Task WaitWhilePausedAsync(CancellationToken cancellationToken = default)
    {
        if (_source is null)
        {
            return Task.CompletedTask;
        }

        var resumed = _source.WhenResumed();
        return resumed.IsCompleted ? Task.CompletedTask : resumed.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Синхронно ждёт снятия паузы — для выделенных потоков сканера.
    /// </summary>
    public void WaitWhilePaused(CancellationToken cancellationToken = default)
    {
        if (_source is null)
        {
            return;
        }

        var resumed = _source.WhenResumed();
        if (!resumed.IsCompleted)
        {
            resumed.Wait(cancellationToken);
        }
    }

    /// <inheritdoc />
    public bool Equals(PauseToken other) => ReferenceEquals(_source, other._source);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PauseToken other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _source?.GetHashCode() ?? 0;

    /// <summary>Равенство токенов: один и тот же источник.</summary>
    public static bool operator ==(PauseToken left, PauseToken right) => left.Equals(right);

    /// <summary>Неравенство токенов.</summary>
    public static bool operator !=(PauseToken left, PauseToken right) => !left.Equals(right);
}

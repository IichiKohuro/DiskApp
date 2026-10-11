namespace DiskApp.UI.Services;

/// <summary>
/// <see cref="IDispatcher"/>, выполняющий действия сразу на вызывающем потоке: для превью и тестов без потока UI.
/// </summary>
public sealed class ImmediateDispatcher : IDispatcher
{
    /// <inheritdoc />
    public bool CheckAccess() => true;

    /// <inheritdoc />
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }

    /// <inheritdoc />
    public Task InvokeAsync(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action();
    }
}

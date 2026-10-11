using System.Runtime.Versioning;

using DiskApp.Core.Abstractions;
using DiskApp.Infrastructure.Platform.Windows;

namespace DiskApp.Desktop.Platform.Windows;

/// <summary>
/// Пробуждение для индексатора (PLAN.md, этап 10.3): выход из сна приходит от системы питания, возврат в сеанс —
/// из служебного окна оболочки (<see cref="MessageWindow.SessionReconnected"/>), которое вызывает
/// <see cref="RaiseSessionReconnected"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DesktopResumeSource : ISystemResumeSource
{
    private readonly WindowsSystemResumeSource _power = new();
    private readonly Lock _gate = new();
    private readonly List<Action> _handlers = [];

    /// <inheritdoc />
    public IDisposable Subscribe(Action onResumed)
    {
        ArgumentNullException.ThrowIfNull(onResumed);

        var power = _power.Subscribe(onResumed);
        lock (_gate)
        {
            _handlers.Add(onResumed);
        }

        return new Subscription(this, onResumed, power);
    }

    /// <summary>Пользователь вернулся в сеанс: подписчики узнают об этом в пуле потоков.</summary>
    public void RaiseSessionReconnected()
    {
        Action[] handlers;
        lock (_gate)
        {
            handlers = [.. _handlers];
        }

        foreach (var handler in handlers)
        {
            ThreadPool.QueueUserWorkItem(static action => action(), handler, preferLocal: false);
        }
    }

    private sealed class Subscription(DesktopResumeSource owner, Action handler, IDisposable power) : IDisposable
    {
        public void Dispose()
        {
            power.Dispose();
            lock (owner._gate)
            {
                owner._handlers.Remove(handler);
            }
        }
    }
}

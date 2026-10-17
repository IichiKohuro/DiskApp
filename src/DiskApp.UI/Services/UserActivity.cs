namespace DiskApp.UI.Services;

/// <summary>Чем занят пользователь — по состоянию уведомлений Windows (PLAN.md, этап 21.2).</summary>
public enum UserBusyState
{
    /// <summary>Уведомления можно показывать.</summary>
    Free,

    /// <summary>Полноэкранное приложение или игра.</summary>
    FullScreen,

    /// <summary>Режим презентации.</summary>
    Presentation,

    /// <summary>«Не беспокоить» или «тихие часы» Windows.</summary>
    DoNotDisturb,

    /// <summary>Пользователя нет: экранная заставка, экран блокировки.</summary>
    Away,
}

/// <summary>Активность пользователя сейчас.</summary>
/// <param name="IdleFor">Сколько нет ввода с клавиатуры и мыши.</param>
/// <param name="Busy">Чем занят.</param>
/// <param name="IsLocked">Сеанс заблокирован.</param>
public readonly record struct UserActivity(TimeSpan IdleFor, UserBusyState Busy, bool IsLocked);

/// <summary>
/// Сигналы занятости для «умного момента» (PLAN.md, этап 21.2, ADR-0013, п. 3): простой ввода, полный экран,
/// презентация, «Не беспокоить», блокировка сеанса. <see cref="Current"/> дешёвый — без диска и сети, его спрашивают раз
/// в минуту. Детектор у каждой оболочки свой; в десктопе — Windows.
/// </summary>
public interface IUserActivityMonitor
{
    /// <summary>Активность сейчас.</summary>
    UserActivity Current { get; }

    /// <summary>Сеанс заблокирован (<c>true</c>) или разблокирован (<c>false</c>); на любом потоке.</summary>
    event EventHandler<bool>? SessionLockChanged;
}

/// <summary>Детектора нет (превью, тесты, другие оболочки): пользователь всегда на месте и свободен.</summary>
public sealed class NullUserActivityMonitor : IUserActivityMonitor
{
    /// <inheritdoc />
    public UserActivity Current => new(TimeSpan.Zero, UserBusyState.Free, false);

    /// <inheritdoc />
    public event EventHandler<bool>? SessionLockChanged
    {
        add { }
        remove { }
    }
}

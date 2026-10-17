using System.Runtime.Versioning;

using DiskApp.UI.Services;

namespace DiskApp.Desktop.Platform.Windows;

/// <summary>
/// Сигналы занятости Windows для «умного момента» (PLAN.md, этап 21.2, ADR-0013, п. 3): простой ввода —
/// <c>GetLastInputInfo</c>, полный экран, презентация и «тихие часы» — <c>SHQueryUserNotificationState</c>, блокировка
/// сеанса — от служебного окна оболочки (<see cref="MessageWindow.SessionLockChanged"/>). Всё — вызовы без диска.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsUserActivityMonitor : IUserActivityMonitor
{
    // Значения QUERY_USER_NOTIFICATION_STATE.
    private const int NotPresent = 1;
    private const int Busy = 2;
    private const int RunningDirect3DFullScreen = 3;
    private const int PresentationMode = 4;
    private const int QuietTime = 6;
    private const int App = 7;

    private volatile bool _locked;

    /// <inheritdoc />
    public event EventHandler<bool>? SessionLockChanged;

    /// <inheritdoc />
    public UserActivity Current => new(IdleFor(), BusyState(), _locked);

    /// <summary>Служебное окно сообщило о блокировке или разблокировке сеанса.</summary>
    public void OnSessionLockChanged(bool locked)
    {
        _locked = locked;
        SessionLockChanged?.Invoke(this, locked);
    }

    private static TimeSpan IdleFor()
    {
        var info = new NativeMethods.LastInputInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.LastInputInfo>() };
        if (!NativeMethods.GetLastInputInfo(ref info))
        {
            return TimeSpan.Zero;
        }

        // Оба счётчика — миллисекунды с загрузки по модулю 2^32: разность без знака переживает переполнение.
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time));
    }

    private static UserBusyState BusyState()
        => NativeMethods.SHQueryUserNotificationState(out var state) != 0 ? UserBusyState.Free : state switch
        {
            NotPresent => UserBusyState.Away,
            Busy or RunningDirect3DFullScreen or App => UserBusyState.FullScreen,
            PresentationMode => UserBusyState.Presentation,
            QuietTime => UserBusyState.DoNotDisturb,
            _ => UserBusyState.Free,
        };
}

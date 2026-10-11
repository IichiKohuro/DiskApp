namespace DiskApp.Desktop.Shell;

/// <summary>
/// Отличает щелчок по значку в трее от двойного щелчка (PLAN.md, этап 18.1, ADR-0011). Avalonia <c>TrayIcon</c> поднимает
/// <c>Clicked</c> на каждое отпускание левой кнопки, и при двойном щелчке оно приходит дважды. Поэтому щелчок срабатывает
/// только через интервал двойного щелчка, если второго за это время не было; второй щелчок внутри интервала — двойной
/// щелчок, и одиночный отменяется.
/// </summary>
/// <param name="interval">Интервал двойного щелчка (в Windows — <c>GetDoubleClickTime</c>).</param>
/// <param name="schedule">Запланировать действие через интервал; результат отменяет его. Подменяется в тестах.</param>
/// <param name="click">Одиночный щелчок.</param>
/// <param name="doubleClick">Двойной щелчок.</param>
internal sealed class TrayClickRecognizer(TimeSpan interval, Func<TimeSpan, Action, IDisposable> schedule, Action click, Action doubleClick)
{
    private IDisposable? _pending;

    /// <summary>Интервал двойного щелчка.</summary>
    public TimeSpan Interval => interval;

    /// <summary>
    /// Отпускание левой кнопки на значке.
    /// </summary>
    public void OnClicked()
    {
        if (_pending is { } pending)
        {
            _pending = null;
            pending.Dispose();
            doubleClick();
            return;
        }

        _pending = schedule(interval, () =>
        {
            _pending = null;
            click();
        });
    }
}

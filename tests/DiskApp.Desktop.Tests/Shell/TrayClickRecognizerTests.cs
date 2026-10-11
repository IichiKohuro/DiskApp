using DiskApp.Desktop.Shell;

namespace DiskApp.Desktop.Tests.Shell;

/// <summary>
/// Щелчок и двойной щелчок по значку в трее (PLAN.md, этап 18.1, ADR-0011): Avalonia поднимает <c>Clicked</c> на каждое
/// отпускание кнопки, поэтому одиночный щелчок ждёт интервал двойного.
/// </summary>
public sealed class TrayClickRecognizerTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    private readonly List<ScheduledAction> _scheduled = [];
    private int _clicks;
    private int _doubleClicks;

    [Fact]
    public void SingleClick_FiresAfterInterval()
    {
        // Arrange
        var recognizer = Create();

        // Act
        recognizer.OnClicked();

        // Assert: до истечения интервала ничего не происходит
        _clicks.ShouldBe(0);
        _scheduled.ShouldHaveSingleItem().Delay.ShouldBe(Interval);

        _scheduled[0].Fire();
        _clicks.ShouldBe(1);
        _doubleClicks.ShouldBe(0);
    }

    [Fact]
    public void SecondClickWithinInterval_IsDoubleClickAndCancelsSingle()
    {
        // Arrange
        var recognizer = Create();

        // Act
        recognizer.OnClicked();
        recognizer.OnClicked();

        // Assert
        _doubleClicks.ShouldBe(1);
        _scheduled[0].IsCancelled.ShouldBeTrue();
        _scheduled[0].Fire();
        _clicks.ShouldBe(0, "отменённый одиночный щелчок не срабатывает");
    }

    [Fact]
    public void ClickAfterSingleFired_StartsNewClick()
    {
        // Arrange
        var recognizer = Create();
        recognizer.OnClicked();
        _scheduled[0].Fire();

        // Act
        recognizer.OnClicked();
        _scheduled[1].Fire();

        // Assert
        _clicks.ShouldBe(2);
        _doubleClicks.ShouldBe(0);
    }

    [Fact]
    public void TripleClick_IsDoubleThenNewPendingClick()
    {
        // Arrange
        var recognizer = Create();

        // Act
        recognizer.OnClicked();
        recognizer.OnClicked();
        recognizer.OnClicked();

        // Assert
        _doubleClicks.ShouldBe(1);
        _scheduled.Count.ShouldBe(2);
        _clicks.ShouldBe(0);
    }

    private TrayClickRecognizer Create() => new(
        Interval,
        (delay, action) =>
        {
            var scheduled = new ScheduledAction(delay, action);
            _scheduled.Add(scheduled);
            return scheduled;
        },
        () => _clicks++,
        () => _doubleClicks++);

    private sealed class ScheduledAction(TimeSpan delay, Action action) : IDisposable
    {
        public TimeSpan Delay => delay;

        public bool IsCancelled { get; private set; }

        public void Fire()
        {
            if (!IsCancelled)
            {
                action();
            }
        }

        public void Dispose() => IsCancelled = true;
    }
}

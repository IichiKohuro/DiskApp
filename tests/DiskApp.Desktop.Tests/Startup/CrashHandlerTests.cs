using DiskApp.Desktop.Startup;

namespace DiskApp.Desktop.Tests.Startup;

/// <summary>
/// Отметка аварийного завершения (PLAN.md, этап 9.5): пишется при падении и читается один раз при следующем запуске.
/// </summary>
public sealed class CrashHandlerTests : IDisposable
{
    private readonly TemporaryDirectory _data = new("crash");

    [Fact]
    public void Marker_WrittenThenTakenOnce()
    {
        // Arrange
        var exception = new InvalidOperationException("сломалось");

        // Act
        CrashHandler.WriteMarker(_data.FullPath, exception, new DateTimeOffset(2026, 10, 11, 9, 30, 0, TimeSpan.FromHours(3)));
        var first = CrashHandler.TakeMarker(_data.FullPath);
        var second = CrashHandler.TakeMarker(_data.FullPath);

        // Assert
        first.ShouldNotBeNull();
        first.ShouldStartWith("2026-10-11 09:30:00 +03:00");
        first.ShouldContain("System.InvalidOperationException: сломалось");
        second.ShouldBeNull("отметка показывается один раз");
    }

    [Fact]
    public void TakeMarker_NoCrash_Null()
        => CrashHandler.TakeMarker(_data.FullPath).ShouldBeNull();

    public void Dispose() => _data.Dispose();
}

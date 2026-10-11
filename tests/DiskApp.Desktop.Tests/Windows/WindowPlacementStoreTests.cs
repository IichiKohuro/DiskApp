using Avalonia;

using DiskApp.Desktop.Windows;

namespace DiskApp.Desktop.Tests.Windows;

/// <summary>
/// Запоминание положения главного окна (PLAN.md, этап 6.7): сохранение и чтение, испорченный файл, проверка,
/// что заголовок попадает на экран после смены мониторов.
/// </summary>
public sealed class WindowPlacementStoreTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new("placement");

    [Fact]
    public void SaveAndLoad_RoundTrip()
    {
        // Arrange
        var store = new WindowPlacementStore(_directory.Resolve("window-state.json"));
        var placement = new WindowPlacement(120, 80, 1280.5, 760, true);

        // Act
        store.SaveMain(placement);
        var loaded = store.LoadMain();

        // Assert
        loaded.ShouldBe(placement);
        File.Exists(_directory.Resolve("window-state.json.tmp")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("не json")]
    [InlineData("""{"main":{"x":1,"y":2,"width":0,"height":0,"maximized":false}}""")]
    public void Load_BrokenOrEmptyFile_ReturnsNull(string content)
    {
        // Arrange
        var path = _directory.CreateFile("window-state.json", content);

        // Act & Assert
        new WindowPlacementStore(path).LoadMain().ShouldBeNull();
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
        => new WindowPlacementStore(_directory.Resolve("none.json")).LoadMain().ShouldBeNull();

    [Theory]
    [InlineData(100, 100, true)]
    [InlineData(1900, 100, false)]
    [InlineData(-990, 100, false)]
    [InlineData(2000, 300, true)]
    [InlineData(100, 1070, false)]
    public void IsReachable_TitleBarMustOverlapAScreen(int x, int y, bool expected)
    {
        // Arrange: экран 1920×1040 слева и второй 1920×1040 правее, со сдвигом вниз на 200
        PixelRect[] screens = [new(0, 0, 1920, 1040), new(1920, 200, 1920, 1040)];

        // Act & Assert
        WindowPlacementStore.IsReachable(new WindowPlacement(x, y, 1000, 700, false), screens).ShouldBe(expected);
    }

    public void Dispose() => _directory.Dispose();
}

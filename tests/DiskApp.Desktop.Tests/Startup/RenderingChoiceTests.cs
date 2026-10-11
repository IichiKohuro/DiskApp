using DiskApp.Desktop.Startup;

namespace DiskApp.Desktop.Tests.Startup;

/// <summary>
/// Выбор рендеринга до запуска Avalonia (PLAN.md, этап 10.2; ADR-0009): по умолчанию программный, аппаратный — по
/// настройке или переменной окружения, нечитаемый файл настроек не мешает запуску.
/// </summary>
public sealed class RenderingChoiceTests : IDisposable
{
    private readonly TemporaryDirectory _data = new("rendering");

    [Theory]
    [InlineData(null, false)]
    [InlineData("""{"appearance":{"theme":"Dark"}}""", false)]
    [InlineData("""{"appearance":{"hardwareAcceleration":false}}""", false)]
    [InlineData("""{"appearance":{"hardwareAcceleration":true}}""", true)]
    [InlineData("""{"appearance":{"hardwareAcceleration":"true"}}""", false)]
    [InlineData("""{ "appearance": { "hardwareAcceleration": true, }, }""", true)]
    [InlineData("""не JSON""", false)]
    [InlineData("""[]""", false)]
    public void Settings_DecideRendering(string? json, bool expected)
    {
        // Arrange
        var path = json is null ? _data.Resolve("settings.json") : _data.CreateFile("settings.json", json);

        // Act & Assert
        RenderingChoice.UseHardwareAcceleration(path, overrideValue: null).ShouldBe(expected);
    }

    [Theory]
    [InlineData("gpu", true)]
    [InlineData("GPU", true)]
    [InlineData("software", false)]
    [InlineData("что-то", true)]
    public void Variable_OverridesSettings(string value, bool expected)
    {
        // Arrange: в настройках ускорение включено; переменная окружения важнее, непонятное значение не учитывается.
        var path = _data.CreateFile("settings.json", """{"appearance":{"hardwareAcceleration":true}}""");

        // Act & Assert
        RenderingChoice.UseHardwareAcceleration(path, value).ShouldBe(expected);
    }

    public void Dispose() => _data.Dispose();
}

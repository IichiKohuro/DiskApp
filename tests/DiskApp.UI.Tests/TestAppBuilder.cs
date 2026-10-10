using Avalonia;
using Avalonia.Headless;

using DiskApp.UI;
using DiskApp.UI.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace DiskApp.UI.Tests;

/// <summary>
/// Конфигурация headless-платформы Avalonia для тестов: то же приложение <see cref="App"/>, что и в десктопе, без окон ОС.
/// </summary>
public static class TestAppBuilder
{
    /// <summary>
    /// Собирает приложение для headless-запуска. Имя и сигнатура метода — соглашение Avalonia.Headless.XUnit.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

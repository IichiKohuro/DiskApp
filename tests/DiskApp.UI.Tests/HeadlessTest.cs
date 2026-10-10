using Avalonia.Headless;

namespace DiskApp.UI.Tests;

/// <summary>
/// Запуск кода теста на UI-потоке headless-сессии Avalonia, общей для всей сборки тестов.
/// Заменяет атрибут <c>[AvaloniaFact]</c> из Avalonia.Headless.XUnit 12.1, который собран под xunit v3 3.x
/// и не работает с xunit v3 4.x; при обновлении пакета помощник можно убрать.
/// </summary>
internal static class HeadlessTest
{
    /// <summary>
    /// Выполняет <paramref name="test"/> на UI-потоке приложения из <see cref="TestAppBuilder"/>.
    /// </summary>
    public static Task RunAsync(Action test)
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(TestAppBuilder).Assembly);
        return session.Dispatch(test, TestContext.Current.CancellationToken);
    }
}

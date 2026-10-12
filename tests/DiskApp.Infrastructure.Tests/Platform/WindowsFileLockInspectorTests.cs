using System.Runtime.Versioning;

using DiskApp.Infrastructure.Platform.Windows;

namespace DiskApp.Infrastructure.Tests.Platform;

/// <summary>
/// Кто держит файл (замечание 2026-10-12): диспетчер перезапуска Windows называет программу с открытым файлом, а у
/// закрытого файла, несуществующего пути и пустой строки — никого, без исключений.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFileLockInspectorTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new("locks");

    [Fact]
    public void FindLockingApplications_OpenFile_NamesHolder_ClosedFile_NoOne()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Диспетчер перезапуска есть только в Windows.");

        // Arrange
        var file = _directory.CreateFile("занят.csv", "a;b");
        var inspector = new WindowsFileLockInspector();
        IReadOnlyList<string> whileOpen;

        // Act: файл держит сам тестовый процесс
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            whileOpen = inspector.FindLockingApplications(file);
        }

        var afterClose = inspector.FindLockingApplications(file);

        // Assert
        whileOpen.ShouldNotBeEmpty();
        whileOpen.ShouldAllBe(static name => name.Length > 0);
        afterClose.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"Z:\нет\такого\файла.txt")]
    public void FindLockingApplications_NoFile_EmptyWithoutThrowing(string path)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Диспетчер перезапуска есть только в Windows.");

        // Act & Assert
        new WindowsFileLockInspector().FindLockingApplications(path).ShouldBeEmpty();
    }

    public void Dispose() => _directory.Dispose();
}

using System.Reflection;

namespace DiskApp.UI.Tests.Architecture;

/// <summary>
/// Правило из docs/ARCHITECTURE.md (раздел 2): интерфейс не зависит от ядра и инфраструктуры,
/// любой экран работает поверх <c>IDiskAppApi</c> из DiskApp.Client.
/// </summary>
public sealed class UiDependencyTests
{
    private static readonly string[] ForbiddenForUiPrefixes =
    [
        "DiskApp.Core",
        "DiskApp.Infrastructure",
        "DiskApp.Server",
        "Microsoft.AspNetCore.App",
        "Microsoft.Data.Sqlite",
    ];

    [Fact]
    public void UiAssembly_ReferencedAssemblies_ContainNoCoreInfrastructureOrServer()
    {
        // Arrange
        var uiAssembly = typeof(App).Assembly;

        // Act
        var violations = uiAssembly.GetReferencedAssemblies()
            .Select(static (AssemblyName name) => name.Name ?? string.Empty)
            .Where(static name => ForbiddenForUiPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToArray();

        // Assert
        violations.ShouldBeEmpty($"DiskApp.UI должен зависеть только от DiskApp.Client. Найдено: {string.Join(", ", violations)}.");
    }
}

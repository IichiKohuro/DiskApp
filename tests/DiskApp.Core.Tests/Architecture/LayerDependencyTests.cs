using System.Reflection;

namespace DiskApp.Core.Tests.Architecture;

/// <summary>
/// Архитектурные правила из docs/ARCHITECTURE.md (раздел 2) и таблицы слоёв README.
/// Проверяются и ссылки между проектами в <c>.csproj</c>, и фактические ссылки скомпилированной сборки ядра.
/// </summary>
public sealed class LayerDependencyTests
{
    private static readonly string[] ForbiddenForCorePrefixes =
    [
        "DiskApp.",
        "Avalonia",
        "FluentAvalonia",
        "Microsoft.AspNetCore",
        "Microsoft.Data.Sqlite",
        "SQLitePCLRaw",
        "Dapper",
    ];

    [Theory]
    [InlineData("DiskApp.Core")]
    [InlineData("DiskApp.Contracts")]
    [InlineData("DiskApp.Infrastructure", "DiskApp.Core")]
    [InlineData("DiskApp.Server", "DiskApp.Core", "DiskApp.Infrastructure", "DiskApp.Contracts")]
    [InlineData("DiskApp.Client", "DiskApp.Contracts")]
    [InlineData("DiskApp.UI", "DiskApp.Client")]
    [InlineData("DiskApp.Desktop", "DiskApp.UI", "DiskApp.Server")]
    public void SourceProject_ProjectReferences_MatchLayerTable(string project, params string[] allowedReferences)
    {
        // Arrange
        var projects = SolutionLayout.SourceProjectReferences;

        // Act
        var found = projects.TryGetValue(project, out var actualReferences);

        // Assert
        found.ShouldBeTrue($"Проект {project} не найден в src/. Если он переименован или удалён, обновите таблицу слоёв в README и этот тест.");
        actualReferences!.ShouldBe(allowedReferences, ignoreOrder: true,
            customMessage: $"Ссылки проекта {project} расходятся с таблицей слоёв README (раздел «Слои и зависимости»).");
    }

    [Fact]
    public void SourceProjects_AllListedInLayerTable()
    {
        // Arrange
        var expected = new[]
        {
            "DiskApp.Core",
            "DiskApp.Contracts",
            "DiskApp.Infrastructure",
            "DiskApp.Server",
            "DiskApp.Client",
            "DiskApp.UI",
            "DiskApp.Desktop",
        };

        // Act
        var actual = SolutionLayout.SourceProjectReferences.Keys;

        // Assert
        actual.ShouldBe(expected, ignoreOrder: true,
            customMessage: "Состав проектов в src/ изменился: обновите таблицу слоёв README, docs/ARCHITECTURE.md и этот тест.");
    }

    [Fact]
    public void CoreAssembly_ReferencedAssemblies_ContainNoSolutionProjectsOrFrameworks()
    {
        // Arrange
        var coreAssembly = typeof(AssemblyMarker).Assembly;

        // Act
        var referencedNames = coreAssembly.GetReferencedAssemblies()
            .Select(static (AssemblyName name) => name.Name ?? string.Empty)
            .ToArray();

        var violations = referencedNames
            .Where(static name => ForbiddenForCorePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToArray();

        // Assert
        violations.ShouldBeEmpty($"DiskApp.Core не должен зависеть от проектов решения, Avalonia, ASP.NET Core и SQLite. Найдено: {string.Join(", ", violations)}.");
    }
}

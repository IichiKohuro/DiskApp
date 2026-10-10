using System.Xml.Linq;

namespace DiskApp.Core.Tests.Architecture;

/// <summary>
/// Доступ к файлам проектов решения из тестов: находит корень репозитория по <c>DiskApp.slnx</c>
/// и читает ссылки между проектами прямо из <c>.csproj</c>.
/// </summary>
internal static class SolutionLayout
{
    private const string SolutionFileName = "DiskApp.slnx";

    /// <summary>
    /// Корень репозитория: ближайшая родительская папка выходного каталога тестов, содержащая файл решения.
    /// </summary>
    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    /// <summary>
    /// Имена проектов в <c>src/</c> и имена проектов решения, на которые каждый из них ссылается через <c>ProjectReference</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> SourceProjectReferences { get; } = ReadSourceProjectReferences();

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Файл {SolutionFileName} не найден ни в одной родительской папке {AppContext.BaseDirectory}.");
    }

    private static Dictionary<string, IReadOnlySet<string>> ReadSourceProjectReferences()
    {
        var sourceDirectory = Path.Combine(RepositoryRoot, "src");
        var result = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

        foreach (var projectFile in Directory.EnumerateFiles(sourceDirectory, "*.csproj", SearchOption.AllDirectories))
        {
            var references = XDocument.Load(projectFile)
                .Descendants("ProjectReference")
                .Select(static element => (string?)element.Attribute("Include"))
                .Where(static include => !string.IsNullOrWhiteSpace(include))
                .Select(static include => Path.GetFileNameWithoutExtension(include!.Replace('\\', '/')))
                .ToHashSet(StringComparer.Ordinal);

            result[Path.GetFileNameWithoutExtension(projectFile)] = references;
        }

        return result;
    }
}

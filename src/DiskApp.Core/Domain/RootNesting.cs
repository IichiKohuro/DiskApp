using DiskApp.Core.Paths;

namespace DiskApp.Core.Domain;

/// <summary>
/// Правило вложенных корней (ADR-0015): путь принадлежит самому глубокому корню, который его содержит. Внешний корень
/// не индексирует поддерево внутреннего (оно для него исключено), а область поиска внешнего корня или его папки
/// включает корни, лежащие внутри. Выключенный корень владеет своим поддеревом так же, как включённый.
/// </summary>
public static class RootNesting
{
    /// <summary>
    /// Корень-владелец пути: самый глубокий из тех, чей путь равен <paramref name="normalizedPath"/> или содержит его;
    /// <c>null</c> — путь вне корней.
    /// </summary>
    public static Root? OwnerOf(string normalizedPath, IEnumerable<Root> roots, PathNormalizer paths)
    {
        ArgumentNullException.ThrowIfNull(normalizedPath);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(paths);

        Root? owner = null;
        foreach (var root in roots)
        {
            if ((paths.Comparer.Equals(root.Path, normalizedPath) || paths.IsNestedIn(normalizedPath, root.Path))
                && (owner is null || root.Path.Length > owner.Path.Length))
            {
                owner = root;
            }
        }

        return owner;
    }

    /// <summary>Корни, лежащие строго внутри папки <paramref name="normalizedFolder"/>, на любой глубине.</summary>
    public static IReadOnlyList<Root> NestedIn(string normalizedFolder, IEnumerable<Root> roots, PathNormalizer paths)
    {
        ArgumentNullException.ThrowIfNull(normalizedFolder);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(paths);

        return roots.Where(candidate => paths.IsNestedIn(candidate.Path, normalizedFolder)).ToArray();
    }

    /// <summary>
    /// Относительные пути корней, лежащих внутри <paramref name="root"/>, — поддеревья, которые он не индексирует.
    /// </summary>
    public static IReadOnlyList<string> NestedRelPaths(Root root, IEnumerable<Root> roots, PathNormalizer paths)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(paths);

        var result = new List<string>();
        foreach (var nested in roots)
        {
            if (paths.TryGetRelativePath(root.Path, nested.Path, out var relative) && relative.Length > 0)
            {
                result.Add(relative);
            }
        }

        return result;
    }
}

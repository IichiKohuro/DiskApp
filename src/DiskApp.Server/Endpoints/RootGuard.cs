using System.Diagnostics.CodeAnalysis;

using DiskApp.Core.Domain;
using DiskApp.Core.Paths;
using DiskApp.Core.Repositories;

namespace DiskApp.Server.Endpoints;

/// <summary>
/// Доступ только внутри добавленных корней (PLAN.md, этап 4.6): путь нормализуется (без <c>..</c>), должен лежать
/// в одном из корней, а если по дороге от корня встречается ссылка (junction, symlink), её настоящая цель тоже
/// должна оказаться внутри корней — так ссылка не выводит за их пределы. Цепочка ссылок проверяется до конца.
/// </summary>
internal sealed class RootGuard(IRootRepository roots, PathNormalizer paths)
{
    private const int MaxLinkDepth = 8;

    /// <summary>Нормализованный путь и корень, внутри которого он лежит; <c>false</c> — путь вне корней или выводит из них.</summary>
    public async Task<(bool Allowed, string? Path, Root? Root)> CheckAsync(string? path, CancellationToken cancellationToken)
    {
        if (!paths.TryNormalize(path, out var normalized))
        {
            return (false, null, null);
        }

        var all = await roots.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var root = FindRoot(all, normalized);
        return root is not null && !EscapesThroughLink(all, root, normalized) ? (true, normalized, root) : (false, normalized, null);
    }

    private Root? FindRoot(IReadOnlyList<Root> all, string normalized) => RootNesting.OwnerOf(normalized, all, paths);

    private bool EscapesThroughLink(IReadOnlyList<Root> all, Root root, string normalized, int depth = 0)
    {
        // Цепочка ссылок длиннее разумной считается выводящей наружу: так не уйти в бесконечный цикл.
        if (depth > MaxLinkDepth)
        {
            return true;
        }

        if (!paths.TryGetRelativePath(root.Path, normalized, out var relative) || relative.Length == 0)
        {
            return false;
        }

        var current = root.Path;
        foreach (var segment in relative.Split(paths.Separator))
        {
            current = paths.Combine(current, segment);
            if (TryResolveLink(current, out var target))
            {
                var remainder = normalized.Length > current.Length ? normalized[(current.Length + 1)..] : string.Empty;
                var real = remainder.Length > 0 ? paths.Combine(target, remainder) : target;

                // Цель ссылки внутри корней, но дальше по пути может встретиться следующая ссылка (этап 10.4).
                return FindRoot(all, real) is not { } realRoot || EscapesThroughLink(all, realRoot, real, depth + 1);
            }
        }

        return false;
    }

    private bool TryResolveLink(string path, [NotNullWhen(true)] out string? target)
    {
        target = null;
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                return false;
            }

            return info.ResolveLinkTarget(returnFinalTarget: true) is { } resolved && paths.TryNormalize(resolved.FullName, out target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

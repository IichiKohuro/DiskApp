using DiskApp.Core.Domain;
using DiskApp.Core.Paths;

namespace DiskApp.Core.Indexing;

/// <summary>
/// Решает, попадает ли запись в индекс (PLAN.md, этап 2.1): glob-шаблоны имени и пути, скрытые и системные объекты,
/// порог размера, глубина. Собирается один раз на сканирование из правил настроек и параметров корня;
/// неизменяем и потокобезопасен.
/// </summary>
/// <remarks>
/// Скрытые и системные объекты исключаются, если корень их не включает (<see cref="Root.IncludeHidden"/>,
/// <see cref="Root.IncludeSystem"/>) или в правилах есть явное <see cref="ExclusionKind.Hidden"/> / <see cref="ExclusionKind.System"/>.
/// Порог размера относится только к файлам. Глубина считается от корня: 1 — верхний уровень.
/// </remarks>
public sealed class ExclusionMatcher
{
    private readonly GlobPattern[] _globs;
    private readonly bool _excludeHidden;
    private readonly bool _excludeSystem;
    private readonly long _maxSize;
    private readonly char _separator;

    private ExclusionMatcher(GlobPattern[] globs, bool excludeHidden, bool excludeSystem, long maxSize, int maxDepth, char separator)
    {
        _globs = globs;
        _excludeHidden = excludeHidden;
        _excludeSystem = excludeSystem;
        _maxSize = maxSize;
        MaxDepth = maxDepth;
        _separator = separator;
    }

    /// <summary>Ничего не исключает.</summary>
    public static ExclusionMatcher None { get; } = new([], false, false, long.MaxValue, int.MaxValue, PathNormalizer.Current.Separator);

    /// <summary>Наибольшая допустимая глубина записи; <see cref="int.MaxValue"/> — без ограничения.</summary>
    public int MaxDepth { get; }

    /// <summary>Шаблоны, по которым исключаются записи.</summary>
    public IReadOnlyList<GlobPattern> Globs => _globs;

    /// <summary>
    /// Собирает правила для корня.
    /// </summary>
    /// <param name="rules">Правила исключения из настроек.</param>
    /// <param name="root">Корень: скрытые, системные, глубина.</param>
    /// <param name="paths">Правила путей: регистр и разделитель.</param>
    public static ExclusionMatcher Create(IEnumerable<ExclusionRule> rules, Root root, PathNormalizer paths)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(paths);

        var ignoreCase = paths.Style == PathStyle.Windows;
        var globs = new List<GlobPattern>();
        var excludeHidden = !root.IncludeHidden;
        var excludeSystem = !root.IncludeSystem;
        var maxSize = long.MaxValue;
        var maxDepth = root.MaxDepth is > 0 ? root.MaxDepth.Value : int.MaxValue;

        foreach (var rule in rules)
        {
            switch (rule.Kind)
            {
                case ExclusionKind.Glob when !string.IsNullOrWhiteSpace(rule.Pattern):
                    globs.Add(new GlobPattern(rule.Pattern, ignoreCase));
                    break;
                case ExclusionKind.Hidden:
                    excludeHidden = true;
                    break;
                case ExclusionKind.System:
                    excludeSystem = true;
                    break;
                case ExclusionKind.Size when rule.Threshold is { } size:
                    maxSize = Math.Min(maxSize, size);
                    break;
                case ExclusionKind.Depth when rule.Threshold is { } depth and > 0:
                    maxDepth = (int)Math.Min(maxDepth, depth);
                    break;
                default:
                    break;
            }
        }

        return new ExclusionMatcher([.. globs], excludeHidden, excludeSystem, maxSize, maxDepth, paths.Separator);
    }

    /// <summary>
    /// Исключена ли запись.
    /// </summary>
    /// <param name="name">Имя.</param>
    /// <param name="relPath">Путь от корня.</param>
    /// <param name="isDirectory">Папка.</param>
    /// <param name="attributes">Атрибуты.</param>
    /// <param name="size">Размер файла в байтах.</param>
    /// <param name="depth">Глубина от корня, с 1.</param>
    public bool IsExcluded(string name, string relPath, bool isDirectory, FileAttributes attributes, long size, int depth)
    {
        if (depth > MaxDepth)
        {
            return true;
        }

        if (_excludeHidden && (attributes & FileAttributes.Hidden) != 0)
        {
            return true;
        }

        if (_excludeSystem && (attributes & FileAttributes.System) != 0)
        {
            return true;
        }

        if (!isDirectory && size > _maxSize)
        {
            return true;
        }

        return MatchesGlob(name, relPath);
    }

    /// <summary>
    /// Исключён ли путь по шаблонам: проверяется каждая папка на пути и сама запись. Атрибуты не учитываются —
    /// для событий наблюдателя их у предков нет, поэтому применяющая сторона дополнительно требует, чтобы родитель был в индексе.
    /// </summary>
    public bool IsPathExcluded(string relPath)
    {
        ArgumentNullException.ThrowIfNull(relPath);

        var depth = 0;
        var start = 0;
        while (start <= relPath.Length)
        {
            var end = relPath.IndexOf(_separator, start);
            if (end < 0)
            {
                end = relPath.Length;
            }

            if (end > start)
            {
                depth++;
                if (depth > MaxDepth || MatchesGlob(relPath[start..end], relPath[..end]))
                {
                    return true;
                }
            }

            start = end + 1;
        }

        return false;
    }

    /// <summary>
    /// Стоит ли заходить внутрь папки на глубине <paramref name="depth"/>: её дети будут на глубине на единицу больше.
    /// </summary>
    public bool CanDescend(int depth) => depth < MaxDepth;

    private bool MatchesGlob(string name, string relPath)
    {
        foreach (var glob in _globs)
        {
            if (glob.IsMatch(name, relPath))
            {
                return true;
            }
        }

        return false;
    }
}

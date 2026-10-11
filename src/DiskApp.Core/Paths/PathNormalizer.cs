using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace DiskApp.Core.Paths;

/// <summary>
/// Приводит абсолютные пути к одному виду, чтобы один и тот же файл не попадал в индекс дважды
/// и сравнение путей было предсказуемым (PLAN.md, этап 1.1):
/// буква диска в верхнем регистре, один вид разделителя, без повторных и завершающих разделителей,
/// сегменты <c>.</c> и <c>..</c> свёрнуты, префикс <c>\\?\</c> снят. Работает только с текстом, к диску не обращается.
/// </summary>
public sealed class PathNormalizer
{
    /// <summary>
    /// Длина пути, начиная с которой Windows требует префикс <c>\\?\</c> (<c>MAX_PATH</c> = 260 вместе с завершающим нулём).
    /// </summary>
    public const int MaxPathLengthWithoutPrefix = 259;

    private const string ExtendedPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";
    private const string UncPrefix = @"\\";
    private static readonly SearchValues<char> WindowsInvalidSegmentChars = SearchValues.Create(['<', '>', ':', '"', '|', '?', '*', '\\', '/']);

    private PathNormalizer(PathStyle style)
    {
        Style = style;
    }

    /// <summary>Правила Windows: <c>C:\…</c> и <c>\\server\share\…</c>.</summary>
    public static PathNormalizer Windows { get; } = new(PathStyle.Windows);

    /// <summary>Правила POSIX: <c>/…</c>.</summary>
    public static PathNormalizer Posix { get; } = new(PathStyle.Posix);

    /// <summary>Правила текущей ОС.</summary>
    public static PathNormalizer Current { get; } = OperatingSystem.IsWindows() ? Windows : Posix;

    /// <summary>Правила записи путей.</summary>
    public PathStyle Style { get; }

    /// <summary>Разделитель сегментов в нормализованном пути.</summary>
    public char Separator => Style == PathStyle.Windows ? '\\' : '/';

    /// <summary>Сравнение путей: без учёта регистра в Windows, с учётом — в POSIX.</summary>
    public StringComparison Comparison => Style == PathStyle.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Компаратор для словарей и множеств путей; согласован с <see cref="Comparison"/>.</summary>
    public StringComparer Comparer => Style == PathStyle.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Нормализует абсолютный путь. Возвращает <c>false</c> для пустого, относительного или некорректного пути
    /// (например, <c>..</c> выше корня или запрещённые символы в сегменте).
    /// </summary>
    public bool TryNormalize(string? path, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var text = path.Trim();
        return Style == PathStyle.Windows ? TryNormalizeWindows(text, out normalized) : TryNormalizePosix(text, out normalized);
    }

    /// <summary>
    /// Нормализует абсолютный путь или бросает исключение. Для пользовательского ввода используйте <see cref="TryNormalize"/>.
    /// </summary>
    /// <exception cref="ArgumentException">Путь пустой, относительный или некорректный.</exception>
    public string Normalize(string path)
        => TryNormalize(path, out var normalized)
            ? normalized
            : throw new ArgumentException($"Ожидался абсолютный путь, получено «{path}».", nameof(path));

    /// <summary>
    /// Является ли путь абсолютным и корректным по правилам стиля.
    /// </summary>
    public bool IsAbsolute(string? path) => TryNormalize(path, out _);

    /// <summary>
    /// Путь в UNC-форме <c>\\server\share…</c> (только Windows).
    /// </summary>
    public bool IsUnc(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Style == PathStyle.Windows
            && path.StartsWith(UncPrefix, StringComparison.Ordinal)
            && !path.StartsWith(ExtendedPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Корень пути: <c>C:\</c>, <c>\\server\share</c> или <c>/</c>. Путь должен быть нормализован.
    /// </summary>
    public string GetRoot(string normalizedPath)
    {
        ArgumentNullException.ThrowIfNull(normalizedPath);

        if (Style == PathStyle.Posix)
        {
            return "/";
        }

        if (IsUnc(normalizedPath))
        {
            var shareEnd = normalizedPath.IndexOf('\\', UncPrefix.Length);
            shareEnd = shareEnd < 0 ? normalizedPath.Length : normalizedPath.IndexOf('\\', shareEnd + 1);
            return shareEnd < 0 ? normalizedPath : normalizedPath[..shareEnd];
        }

        return normalizedPath[..3];
    }

    /// <summary>
    /// Является ли нормализованный путь корнем тома или UNC-ресурса.
    /// </summary>
    public bool IsRoot(string normalizedPath) => Comparer.Equals(GetRoot(normalizedPath), normalizedPath);

    /// <summary>
    /// Последний сегмент пути; для корня — сам корень (<c>C:\</c>), как показывает Проводник.
    /// </summary>
    public string GetFileName(string normalizedPath)
    {
        ArgumentNullException.ThrowIfNull(normalizedPath);

        if (IsRoot(normalizedPath))
        {
            return normalizedPath;
        }

        var index = normalizedPath.LastIndexOf(Separator);
        return index < 0 ? normalizedPath : normalizedPath[(index + 1)..];
    }

    /// <summary>
    /// Родительская папка нормализованного пути; <c>null</c> для корня.
    /// </summary>
    public string? GetParent(string normalizedPath)
    {
        ArgumentNullException.ThrowIfNull(normalizedPath);

        var root = GetRoot(normalizedPath);
        if (Comparer.Equals(root, normalizedPath))
        {
            return null;
        }

        var index = normalizedPath.LastIndexOf(Separator);
        var parent = normalizedPath[..index];
        return parent.Length < root.Length ? root : parent;
    }

    /// <summary>
    /// Склеивает нормализованный корень и относительный путь. Пустой относительный путь даёт сам корень.
    /// Разделители относительного пути приводятся к <see cref="Separator"/>.
    /// </summary>
    public string Combine(string normalizedRoot, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(normalizedRoot);
        ArgumentNullException.ThrowIfNull(relativePath);

        var relative = NormalizeRelative(relativePath);
        if (relative.Length == 0)
        {
            return normalizedRoot;
        }

        return normalizedRoot.EndsWith(Separator)
            ? normalizedRoot + relative
            : normalizedRoot + Separator + relative;
    }

    /// <summary>
    /// Приводит относительный путь к виду без ведущих и завершающих разделителей и с разделителем стиля.
    /// Сегменты <c>.</c> и <c>..</c> не раскрываются: относительные пути строит сканер, а не пользователь.
    /// </summary>
    public string NormalizeRelative(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);

        var text = Style == PathStyle.Windows ? relativePath.Replace('/', '\\') : relativePath;
        return text.Trim(Separator);
    }

    /// <summary>
    /// Путь <paramref name="normalizedPath"/> относительно <paramref name="normalizedRoot"/>:
    /// пустая строка, если это сам корень; <c>false</c>, если путь лежит вне корня.
    /// </summary>
    public bool TryGetRelativePath(string normalizedRoot, string normalizedPath, [NotNullWhen(true)] out string? relativePath)
    {
        ArgumentNullException.ThrowIfNull(normalizedRoot);
        ArgumentNullException.ThrowIfNull(normalizedPath);

        if (Comparer.Equals(normalizedRoot, normalizedPath))
        {
            relativePath = string.Empty;
            return true;
        }

        var prefix = normalizedRoot.EndsWith(Separator) ? normalizedRoot : normalizedRoot + Separator;
        if (normalizedPath.StartsWith(prefix, Comparison))
        {
            relativePath = normalizedPath[prefix.Length..];
            return true;
        }

        relativePath = null;
        return false;
    }

    /// <summary>
    /// Лежит ли <paramref name="normalizedPath"/> внутри <paramref name="normalizedAncestor"/> (строго, не совпадая с ним).
    /// </summary>
    public bool IsNestedIn(string normalizedPath, string normalizedAncestor)
        => TryGetRelativePath(normalizedAncestor, normalizedPath, out var relative) && relative.Length > 0;

    /// <summary>
    /// Для Windows добавляет префикс <c>\\?\</c> (или <c>\\?\UNC\</c>) путям длиннее <see cref="MaxPathLengthWithoutPrefix"/>,
    /// чтобы Win32 принимал длинные пути; короткие пути и POSIX возвращаются как есть.
    /// </summary>
    public string ToExtendedLength(string normalizedPath)
    {
        ArgumentNullException.ThrowIfNull(normalizedPath);

        if (Style == PathStyle.Posix
            || normalizedPath.Length <= MaxPathLengthWithoutPrefix
            || normalizedPath.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
        {
            return normalizedPath;
        }

        return IsUnc(normalizedPath)
            ? ExtendedUncPrefix + normalizedPath[UncPrefix.Length..]
            : ExtendedPrefix + normalizedPath;
    }

    private static bool TryNormalizeWindows(string text, out string? normalized)
    {
        normalized = null;

        if (text.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            text = UncPrefix + text[ExtendedUncPrefix.Length..];
        }
        else if (text.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
        {
            text = text[ExtendedPrefix.Length..];
        }

        text = text.Replace('/', '\\');

        string head;
        IEnumerable<string> segments;
        bool isUnc;

        if (text.StartsWith(UncPrefix, StringComparison.Ordinal))
        {
            var parts = text[UncPrefix.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            // «\\.\» и «\\?\» — пространство имён устройств (диски, каналы), а не сетевой ресурс (этап 10.4).
            if (parts.Length < 2 || parts[0] is "." or "?" || !IsValidSegment(parts[0]) || !IsValidSegment(parts[1]))
            {
                return false;
            }

            isUnc = true;
            head = UncPrefix + parts[0] + '\\' + parts[1];
            segments = parts.Skip(2);
        }
        else if (text.Length >= 3 && char.IsAsciiLetter(text[0]) && text[1] == ':' && text[2] == '\\')
        {
            isUnc = false;
            head = char.ToUpperInvariant(text[0]) + ":";
            segments = text[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        }
        else
        {
            // «C:» без разделителя — путь относительно текущей папки диска; «C:file» — тоже. Оба не абсолютны.
            return false;
        }

        if (!TryResolveSegments(segments, IsValidSegment, out var resolved))
        {
            return false;
        }

        normalized = isUnc
            ? resolved.Count == 0 ? head : head + '\\' + string.Join('\\', resolved)
            : resolved.Count == 0 ? head + '\\' : head + '\\' + string.Join('\\', resolved);
        return true;
    }

    private static bool TryNormalizePosix(string text, out string? normalized)
    {
        normalized = null;
        if (text[0] != '/')
        {
            return false;
        }

        var segments = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (!TryResolveSegments(segments, static segment => !segment.Contains('\0', StringComparison.Ordinal), out var resolved))
        {
            return false;
        }

        normalized = resolved.Count == 0 ? "/" : "/" + string.Join('/', resolved);
        return true;
    }

    private static bool TryResolveSegments(IEnumerable<string> segments, Func<string, bool> isValid, out List<string> resolved)
    {
        resolved = [];
        foreach (var segment in segments)
        {
            switch (segment)
            {
                case ".":
                    continue;
                case "..":
                    if (resolved.Count == 0)
                    {
                        return false;
                    }

                    resolved.RemoveAt(resolved.Count - 1);
                    continue;
                default:
                    if (!isValid(segment))
                    {
                        return false;
                    }

                    resolved.Add(segment);
                    continue;
            }
        }

        return true;
    }

    private static bool IsValidSegment(string segment)
    {
        if (segment.AsSpan().ContainsAny(WindowsInvalidSegmentChars))
        {
            return false;
        }

        foreach (var character in segment)
        {
            if (char.IsControl(character))
            {
                return false;
            }
        }

        return true;
    }
}

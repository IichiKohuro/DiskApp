using System.Text;
using System.Text.RegularExpressions;

namespace DiskApp.Core.Indexing;

/// <summary>
/// Glob-шаблон имени или относительного пути: <c>*</c> — любые символы, кроме разделителя, <c>?</c> — один такой символ,
/// <c>**</c> — любые символы, включая разделители. Шаблон без разделителя сравнивается с именем записи, с разделителем —
/// с путём от корня (<c>docs/*.tmp</c>, <c>**/cache</c>). Разделитель в шаблоне и пути — <c>/</c> или <c>\</c>.
/// Частые случаи (точное имя, <c>*.ext</c>, <c>prefix*</c>) проверяются без регулярного выражения.
/// </summary>
public sealed class GlobPattern
{
    private readonly StringComparison _comparison;
    private readonly MatchKind _kind;
    private readonly string _literal;
    private readonly Regex? _regex;

    /// <summary>
    /// Разбирает шаблон.
    /// </summary>
    /// <param name="pattern">Шаблон.</param>
    /// <param name="ignoreCase">Сравнивать без учёта регистра (Windows).</param>
    public GlobPattern(string pattern, bool ignoreCase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        Pattern = pattern.Trim();
        var normalized = Pattern.Replace('\\', '/').Trim('/');
        MatchesPath = normalized.Contains('/', StringComparison.Ordinal);
        _comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (!MatchesPath && normalized.IndexOfAny(['*', '?']) < 0)
        {
            _kind = MatchKind.Exact;
            _literal = normalized;
        }
        else if (!MatchesPath && normalized.StartsWith('*') && normalized.IndexOfAny(['*', '?'], 1) < 0)
        {
            _kind = MatchKind.Suffix;
            _literal = normalized[1..];
        }
        else if (!MatchesPath && normalized.EndsWith('*') && normalized.IndexOfAny(['*', '?']) == normalized.Length - 1)
        {
            _kind = MatchKind.Prefix;
            _literal = normalized[..^1];
        }
        else
        {
            _kind = MatchKind.Regex;
            _literal = normalized;
            var options = RegexOptions.CultureInvariant | RegexOptions.Compiled | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
            _regex = new Regex(ToRegex(normalized), options, TimeSpan.FromMilliseconds(100));
        }
    }

    /// <summary>Исходный текст шаблона.</summary>
    public string Pattern { get; }

    /// <summary>Шаблон сравнивается с путём от корня, а не с именем.</summary>
    public bool MatchesPath { get; }

    /// <summary>
    /// Подходит ли запись под шаблон.
    /// </summary>
    /// <param name="name">Имя записи.</param>
    /// <param name="relPath">Путь от корня с любым разделителем; нужен только для шаблонов с разделителем.</param>
    public bool IsMatch(string name, string relPath)
    {
        ArgumentNullException.ThrowIfNull(name);

        return _kind switch
        {
            MatchKind.Exact => string.Equals(name, _literal, _comparison),
            MatchKind.Suffix => name.EndsWith(_literal, _comparison),
            MatchKind.Prefix => name.StartsWith(_literal, _comparison),
            _ => _regex!.IsMatch(MatchesPath ? relPath.Replace('\\', '/') : name),
        };
    }

    /// <inheritdoc />
    public override string ToString() => Pattern;

    private static string ToRegex(string glob)
    {
        var builder = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var character = glob[i];
            switch (character)
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    // «**/» — ноль или больше папок; «**» в конце или середине — что угодно.
                    if (i + 2 < glob.Length && glob[i + 2] == '/')
                    {
                        builder.Append("(?:.*/)?");
                        i += 2;
                    }
                    else
                    {
                        builder.Append(".*");
                        i++;
                    }

                    break;
                case '*':
                    builder.Append("[^/]*");
                    break;
                case '?':
                    builder.Append("[^/]");
                    break;
                default:
                    builder.Append(Regex.Escape(character.ToString()));
                    break;
            }
        }

        return builder.Append('$').ToString();
    }

    private enum MatchKind
    {
        Exact,
        Suffix,
        Prefix,
        Regex,
    }
}

using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace DiskApp.Core.Search;

/// <summary>
/// Разбор строки поиска (PLAN.md, этап 3.1; README, «Синтаксис поиска»): слова, фразы в кавычках, фильтры
/// <c>ключ:значение</c> (значение тоже можно взять в кавычки), отрицание <c>-</c>, регулярное выражение <c>/…/</c>.
/// Ключи и значения понимаются на русском и английском. Разбор терпим к ошибкам: незакрытая кавычка — фраза до конца
/// строки, неизвестный ключ — обычный текст, непонятое значение — текст с замечанием, фильтр без значения — пропускается
/// (пользователь ещё печатает). Каждый токен сохраняет позицию для подсветки и подсказок.
/// </summary>
public static class QueryParser
{
    private static readonly FrozenDictionary<string, SearchFilterKey> Keys = new Dictionary<string, SearchFilterKey>(StringComparer.Ordinal)
    {
        ["ext"] = SearchFilterKey.Extension,
        ["extension"] = SearchFilterKey.Extension,
        ["расширение"] = SearchFilterKey.Extension,
        ["расш"] = SearchFilterKey.Extension,
        ["type"] = SearchFilterKey.Kind,
        ["kind"] = SearchFilterKey.Kind,
        ["тип"] = SearchFilterKey.Kind,
        ["вид"] = SearchFilterKey.Kind,
        ["in"] = SearchFilterKey.Folder,
        ["folder"] = SearchFilterKey.Folder,
        ["папка"] = SearchFilterKey.Folder,
        ["в"] = SearchFilterKey.Folder,
        ["root"] = SearchFilterKey.Root,
        ["корень"] = SearchFilterKey.Root,
        ["size"] = SearchFilterKey.Size,
        ["размер"] = SearchFilterKey.Size,
        ["modified"] = SearchFilterKey.Modified,
        ["mod"] = SearchFilterKey.Modified,
        ["changed"] = SearchFilterKey.Modified,
        ["изменен"] = SearchFilterKey.Modified,
        ["изменено"] = SearchFilterKey.Modified,
        ["created"] = SearchFilterKey.Created,
        ["создан"] = SearchFilterKey.Created,
        ["создано"] = SearchFilterKey.Created,
        ["is"] = SearchFilterKey.Attribute,
        ["это"] = SearchFilterKey.Attribute,
        ["tag"] = SearchFilterKey.Tag,
        ["тег"] = SearchFilterKey.Tag,
        ["метка"] = SearchFilterKey.Tag,
        ["fav"] = SearchFilterKey.Favorite,
        ["favorite"] = SearchFilterKey.Favorite,
        ["избранное"] = SearchFilterKey.Favorite,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Канонические ключи фильтров для подсказок и <see cref="SearchQuery.ToString"/>.</summary>
    public static IReadOnlyList<string> CanonicalKeys { get; } = ["ext", "type", "in", "root", "size", "modified", "created", "is", "tag", "fav"];

    /// <summary>Канонический ключ фильтра.</summary>
    public static string CanonicalKey(SearchFilterKey key) => key switch
    {
        SearchFilterKey.Extension => "ext",
        SearchFilterKey.Kind => "type",
        SearchFilterKey.Folder => "in",
        SearchFilterKey.Root => "root",
        SearchFilterKey.Size => "size",
        SearchFilterKey.Modified => "modified",
        SearchFilterKey.Created => "created",
        SearchFilterKey.Attribute => "is",
        SearchFilterKey.Tag => "tag",
        _ => "fav",
    };

    /// <summary>
    /// Ключ фильтра по слову (любой синоним, без учёта регистра); <c>false</c> — это не ключ.
    /// </summary>
    public static bool TryGetKey(string word, out SearchFilterKey key) => Keys.TryGetValue(SearchText.Fold(word), out key);

    /// <summary>
    /// Разбирает строку.
    /// </summary>
    public static SearchQuery Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return SearchQuery.Empty;
        }

        var terms = new List<SearchTerm>();
        var filters = new List<SearchFilter>();
        var tokens = new List<QueryToken>();
        var warnings = new List<string>();
        string? regex = null;
        var i = 0;

        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }

            var start = i;
            var negated = text[i] == '-' && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]) && text[i + 1] != '-';
            if (negated)
            {
                i++;
            }

            if (text[i] == '"')
            {
                var close = text.IndexOf('"', i + 1);
                var end = close < 0 ? text.Length : close;
                var phrase = text[(i + 1)..end].Trim();
                i = close < 0 ? text.Length : close + 1;
                if (phrase.Length > 0)
                {
                    terms.Add(new SearchTerm(phrase, IsPhrase: true, IsNegated: negated));
                    tokens.Add(new QueryToken(QueryTokenKind.Phrase, start, i - start, text[start..i]));
                }

                continue;
            }

            var bodyStart = i;
            var colon = -1;
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
            {
                if (text[i] == ':' && colon < 0)
                {
                    colon = i;
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        var close = text.IndexOf('"', i + 2);
                        i = close < 0 ? text.Length : close + 1;
                        break;
                    }
                }

                i++;
            }

            var word = text[bodyStart..i];
            var tokenText = text[start..i];
            if (word.Length == 0)
            {
                continue;
            }

            if (word.Length >= 3 && word[0] == '/' && word[^1] == '/')
            {
                var pattern = word[1..^1];
                if (IsValidRegex(pattern))
                {
                    regex = pattern;
                    tokens.Add(new QueryToken(QueryTokenKind.Regex, start, i - start, tokenText));
                    continue;
                }

                warnings.Add($"Регулярное выражение «{pattern}» не разобрано — ищу как текст.");
            }

            if (colon > bodyStart && TryGetKey(text[bodyStart..colon], out var key))
            {
                var value = Unquote(text[(colon + 1)..i]);
                if (value.Length == 0)
                {
                    tokens.Add(new QueryToken(QueryTokenKind.IncompleteFilter, start, i - start, tokenText));
                    continue;
                }

                if (IsValidValue(key, value))
                {
                    filters.Add(new SearchFilter(key, value, negated));
                    tokens.Add(new QueryToken(QueryTokenKind.Filter, start, i - start, tokenText));
                    continue;
                }

                warnings.Add($"Не понял значение фильтра «{text[bodyStart..i]}» — ищу как текст.");
            }

            terms.Add(new SearchTerm(word, IsNegated: negated));
            tokens.Add(new QueryToken(QueryTokenKind.Term, start, i - start, tokenText));
        }

        return new SearchQuery
        {
            Terms = terms,
            Filters = filters,
            RegexPattern = regex,
            Tokens = tokens,
            Warnings = warnings,
        };
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('"'))
        {
            trimmed = trimmed.EndsWith('"') && trimmed.Length > 1 ? trimmed[1..^1] : trimmed[1..];
        }

        return trimmed.Trim();
    }

    private static bool IsValidValue(SearchFilterKey key, string value) => key switch
    {
        SearchFilterKey.Size => FilterValues.TryParseSize(value, out _),
        SearchFilterKey.Modified or SearchFilterKey.Created => FilterValues.TryParseDate(value, DateTimeOffset.UtcNow, TimeZoneInfo.Utc, out _),
        SearchFilterKey.Kind => FilterValues.TryParseKinds(value, out _),
        SearchFilterKey.Attribute => FilterValues.TryParseAttribute(value, out _),
        SearchFilterKey.Favorite => FilterValues.TryParseFavorite(value, out _),
        SearchFilterKey.Extension => FilterValues.ParseExtensions(value).Count > 0,
        _ => true,
    };

    private static bool IsValidRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

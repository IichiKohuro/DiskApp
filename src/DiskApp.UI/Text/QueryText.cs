using System.Text;

namespace DiskApp.UI.Text;

/// <summary>
/// Текст запроса для чипов фильтров (PLAN.md, этап 7.5): деление на слова с учётом кавычек, ключ фильтра слова,
/// замена фильтра группы. Разбор с полной грамматикой — на сервере; здесь только то, что нужно чипам.
/// </summary>
public static class QueryText
{
    /// <summary>
    /// Слова запроса; фраза в кавычках — одно слово вместе с кавычками.
    /// </summary>
    public static IReadOnlyList<string> Tokenize(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var tokens = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var c in query)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }

            if (char.IsWhiteSpace(c) && !quoted)
            {
                Flush();
                continue;
            }

            current.Append(c);
        }

        Flush();
        return tokens;

        void Flush()
        {
            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }
    }

    /// <summary>
    /// Ключ фильтра слова в нижнем регистре без знака отрицания: <c>type</c> для <c>-type:image</c>; пусто, если это не фильтр.
    /// </summary>
    public static string KeyOf(string token)
    {
        ArgumentNullException.ThrowIfNull(token);

        var body = token.StartsWith('-') ? token[1..] : token;
        var colon = body.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && !body.StartsWith('"') ? body[..colon].ToLowerInvariant() : string.Empty;
    }

    /// <summary>
    /// Запрос, где фильтры с ключом <paramref name="key"/> (кроме отрицаний) заменены на <paramref name="token"/>;
    /// <c>null</c> — просто убрать их.
    /// </summary>
    public static string WithFilter(string query, string key, string? token)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(key);

        var kept = Tokenize(query).Where(item => item.StartsWith('-') || !string.Equals(KeyOf(item), key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (token is not null)
        {
            kept.Add(token);
        }

        return string.Join(' ', kept);
    }

    /// <summary>
    /// Есть ли в запросе слово <paramref name="token"/> (без учёта регистра).
    /// </summary>
    public static bool Contains(string query, string token)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Tokenize(query).Any(item => string.Equals(item, token, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Значение для фильтра: в кавычках, если в нём есть пробел.
    /// </summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", string.Empty, StringComparison.Ordinal)}\"" : value;
    }
}

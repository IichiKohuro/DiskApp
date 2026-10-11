using System.Globalization;

using DiskApp.Core.Domain;

namespace DiskApp.Core.Search;

/// <summary>
/// Ранжирование кандидатов (PLAN.md, этап 3.3): тип совпадения терма с именем (точное &gt; начало имени &gt; начало слова
/// &gt; подстрока &gt; нечёткое), доля покрытия имени, frecency, избранное, штраф за глубину, свежесть изменения.
/// Веса задаёт пресет. Встроенный ранг FTS (bm25) не используется: на частых триграммах он стоит сотни миллисекунд
/// и не отражает полезность для имён файлов (ADR-0005).
/// </summary>
public sealed class SearchRanker(RankingWeights weights, DateTimeOffset now, TimeSpan frecencyHalfLife)
{
    private const double ExactScore = 1.0;
    private const double PrefixScore = 0.85;
    private const double WordStartScore = 0.65;
    private const double SubstringScore = 0.3;
    private const double FuzzyCeiling = 0.25;

    /// <summary>
    /// Оценивает кандидата.
    /// </summary>
    /// <param name="candidate">Кандидат.</param>
    /// <param name="terms">Свёрнутые положительные термы.</param>
    /// <param name="fullPath">Полный путь записи — для подсветки совпадений в папке.</param>
    /// <param name="includeReason">Собрать объяснение оценки.</param>
    public RankedCandidate Rank(SearchCandidate candidate, IReadOnlyList<string> terms, string fullPath, bool includeReason)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(fullPath);

        var entry = candidate.Entry;
        var name = entry.Name;
        var mapped = SearchText.FoldMapped(name);
        var folded = mapped.Folded;
        var composed = mapped.Composed;
        var stemLength = entry.IsDirectory || entry.Extension.Length == 0 ? composed.Length : Math.Max(1, composed.Length - entry.Extension.Length - 1);
        var nameRanges = new List<TextRange>();
        var best = MatchKind.None;
        double match = 0;
        var covered = 0;
        var minSimilarity = 1.0;

        foreach (var term in terms)
        {
            var (kind, termScore, ranges) = MatchTerm(term, folded, composed, stemLength);
            match += termScore;
            nameRanges.AddRange(ranges.Select(mapped.ToSource));
            minSimilarity = Math.Min(minSimilarity, kind switch
            {
                MatchKind.None => 0,
                MatchKind.Fuzzy => termScore / FuzzyCeiling,
                _ => 1,
            });
            if (kind < best || best == MatchKind.None)
            {
                best = kind == MatchKind.None ? best : kind;
            }

            if (kind is not MatchKind.None and not MatchKind.Fuzzy)
            {
                covered += term.Length;
            }
        }

        match = terms.Count > 0 ? match / terms.Count : 0;
        var coverage = terms.Count > 0 ? Math.Min(1.0, (double)covered / stemLength) : 0;
        var frecencyRaw = new UsageStat(entry.Id, candidate.OpenCount, candidate.LastOpenedUtc).Frecency(now, frecencyHalfLife);
        var frecency = frecencyRaw / (1 + frecencyRaw);
        var favorite = candidate.IsFavorite ? 1.0 : 0.0;
        var depth = Math.Min(entry.Depth, 20) / 20.0;
        var ageDays = Math.Max(0, (now - entry.ModifiedUtc).TotalDays);
        var recency = Math.Pow(0.5, ageDays / 30);

        // История и свежесть усиливают совпадение, а не заменяют его: при слабом совпадении (подстрока посреди слова)
        // часто открываемый файл не должен обгонять файл, имя которого начинается с запроса.
        var historyFactor = terms.Count > 0 ? 0.4 + (0.6 * match) : 1.0;
        var score = (weights.Match * match)
            + (weights.Coverage * coverage)
            + (historyFactor * ((weights.Frecency * frecency) + (weights.Favorite * favorite) + (weights.Recency * recency)))
            - (weights.Depth * depth);

        var reason = includeReason ? Describe(best, match, coverage, frecency, candidate.IsFavorite, depth, recency) : null;
        return new RankedCandidate(candidate, score, Merge(nameRanges), PathRanges(terms, fullPath, name.Length), best, reason)
        {
            MinTermSimilarity = terms.Count > 0 ? minSimilarity : 1,
        };
    }

    private static (MatchKind Kind, double Score, IReadOnlyList<TextRange> Ranges) MatchTerm(string term, string folded, string original, int stemLength)
    {
        if (term.Length == 0)
        {
            return (MatchKind.None, 0, []);
        }

        if ((folded.Length == term.Length && string.Equals(folded, term, StringComparison.Ordinal))
            || (stemLength == term.Length && folded.StartsWith(term, StringComparison.Ordinal)))
        {
            return (MatchKind.Exact, ExactScore, [new TextRange(0, term.Length)]);
        }

        if (folded.StartsWith(term, StringComparison.Ordinal))
        {
            return (MatchKind.Prefix, PrefixScore, [new TextRange(0, term.Length)]);
        }

        var first = -1;
        for (var index = folded.IndexOf(term, StringComparison.Ordinal); index >= 0; index = folded.IndexOf(term, index + 1, StringComparison.Ordinal))
        {
            if (first < 0)
            {
                first = index;
            }

            if (SearchText.IsWordStart(original, index))
            {
                return (MatchKind.WordStart, WordStartScore, [new TextRange(index, term.Length)]);
            }
        }

        if (first >= 0)
        {
            return (MatchKind.Substring, SubstringScore, [new TextRange(first, term.Length)]);
        }

        var fuzzy = FuzzyMatcher.Match(term, folded, original);
        if (fuzzy.Score <= 0)
        {
            return (MatchKind.None, 0, []);
        }

        return (MatchKind.Fuzzy, FuzzyCeiling * fuzzy.Score, ToRanges(fuzzy.Positions));
    }

    private static List<TextRange> PathRanges(IReadOnlyList<string> terms, string fullPath, int nameLength)
    {
        var directoryLength = Math.Max(0, fullPath.Length - nameLength - 1);
        if (directoryLength == 0 || terms.Count == 0)
        {
            return [];
        }

        var mapped = SearchText.FoldMapped(fullPath[..directoryLength]);
        var ranges = new List<TextRange>();
        foreach (var term in terms)
        {
            var index = mapped.Folded.IndexOf(term, StringComparison.Ordinal);
            if (index >= 0)
            {
                ranges.Add(mapped.ToSource(new TextRange(index, term.Length)));
            }
        }

        return Merge(ranges);
    }

    private static List<TextRange> ToRanges(IReadOnlyList<int> positions)
    {
        var ranges = new List<TextRange>();
        var i = 0;
        while (i < positions.Count)
        {
            var start = positions[i];
            var length = 1;
            while (i + length < positions.Count && positions[i + length] == start + length)
            {
                length++;
            }

            ranges.Add(new TextRange(start, length));
            i += length;
        }

        return ranges;
    }

    private static List<TextRange> Merge(List<TextRange> ranges)
    {
        if (ranges.Count <= 1)
        {
            return ranges;
        }

        ranges.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        var merged = new List<TextRange> { ranges[0] };
        for (var i = 1; i < ranges.Count; i++)
        {
            var last = merged[^1];
            var current = ranges[i];
            if (current.Start <= last.End)
            {
                merged[^1] = new TextRange(last.Start, Math.Max(last.End, current.End) - last.Start);
            }
            else
            {
                merged.Add(current);
            }
        }

        return merged;
    }

    private static string Describe(MatchKind best, double match, double coverage, double frecency, bool favorite, double depth, double recency)
    {
        var parts = new List<string>
        {
            best switch
            {
                MatchKind.Exact => "точное имя",
                MatchKind.Prefix => "начало имени",
                MatchKind.WordStart => "начало слова",
                MatchKind.Substring => "подстрока",
                MatchKind.Fuzzy => "нечёткое",
                _ => "без термов",
            },
            string.Create(CultureInfo.InvariantCulture, $"совпадение {match:F2}"),
            string.Create(CultureInfo.InvariantCulture, $"покрытие {coverage:F2}"),
        };

        if (frecency > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"frecency {frecency:F2}"));
        }

        if (favorite)
        {
            parts.Add("избранное");
        }

        parts.Add(string.Create(CultureInfo.InvariantCulture, $"глубина {depth:F2}"));
        parts.Add(string.Create(CultureInfo.InvariantCulture, $"свежесть {recency:F2}"));
        return string.Join(" · ", parts);
    }
}

/// <summary>
/// Веса компонентов оценки.
/// </summary>
/// <param name="Match">Тип совпадения термов с именем.</param>
/// <param name="Coverage">Доля имени, покрытая термами.</param>
/// <param name="Frecency">Частота и давность открытия.</param>
/// <param name="Favorite">Избранное.</param>
/// <param name="Depth">Штраф за глубину пути.</param>
/// <param name="Recency">Свежесть изменения.</param>
public sealed record RankingWeights(double Match, double Coverage, double Frecency, double Favorite, double Depth, double Recency)
{
    /// <summary>Веса пресета (PLAN.md, этап 3.3).</summary>
    public static RankingWeights For(RankingPreset preset) => preset switch
    {
        RankingPreset.ByName => new RankingWeights(Match: 1.0, Coverage: 0.5, Frecency: 0.2, Favorite: 0.2, Depth: 0.15, Recency: 0.05),
        RankingPreset.Recent => new RankingWeights(Match: 0.6, Coverage: 0.2, Frecency: 0.8, Favorite: 0.3, Depth: 0.05, Recency: 0.6),
        _ => new RankingWeights(Match: 1.0, Coverage: 0.3, Frecency: 0.6, Favorite: 0.4, Depth: 0.1, Recency: 0.2),
    };

    /// <summary>Пресет по имени из настроек (<c>balanced</c>, <c>name</c>, <c>recent</c>); неизвестное — сбалансированный.</summary>
    public static RankingPreset ParsePreset(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "name" or "byname" or "по имени" => RankingPreset.ByName,
        "recent" or "недавнее" => RankingPreset.Recent,
        _ => RankingPreset.Balanced,
    };
}

/// <summary>
/// Пресеты весов ранжирования.
/// </summary>
public enum RankingPreset
{
    /// <summary>Сбалансированно.</summary>
    Balanced,

    /// <summary>По имени: совпадение важнее истории.</summary>
    ByName,

    /// <summary>Недавнее: открытое и изменённое недавно — выше.</summary>
    Recent,
}

/// <summary>
/// Тип совпадения терма с именем; порядок — от лучшего.
/// </summary>
public enum MatchKind
{
    /// <summary>Имя (или имя без расширения) совпадает с термом.</summary>
    Exact,

    /// <summary>Имя начинается с терма.</summary>
    Prefix,

    /// <summary>Терм начинается с границы слова.</summary>
    WordStart,

    /// <summary>Терм внутри слова.</summary>
    Substring,

    /// <summary>Нечёткое совпадение (опечатка, пропуски).</summary>
    Fuzzy,

    /// <summary>Нет совпадения в имени.</summary>
    None,
}

/// <summary>
/// Оценённый кандидат.
/// </summary>
/// <param name="Candidate">Кандидат.</param>
/// <param name="Score">Оценка.</param>
/// <param name="NameHighlights">Подсветка в имени.</param>
/// <param name="PathHighlights">Подсветка в папке полного пути.</param>
/// <param name="BestMatch">Лучший тип совпадения среди термов.</param>
/// <param name="Reason">Объяснение оценки или <c>null</c>.</param>
public sealed record RankedCandidate(
    SearchCandidate Candidate,
    double Score,
    IReadOnlyList<TextRange> NameHighlights,
    IReadOnlyList<TextRange> PathHighlights,
    MatchKind BestMatch,
    string? Reason)
{
    /// <summary>
    /// Худшее сходство среди термов: 1 — все совпали точно (подстрокой), 0 — какой-то не совпал вовсе,
    /// между — нечёткие. По нему отсекаются слабые нечёткие совпадения.
    /// </summary>
    public double MinTermSimilarity { get; init; } = 1;
}

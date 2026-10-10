namespace DiskApp.Core.Search;

/// <summary>
/// Нечёткое совпадение (PLAN.md, этап 3.4). Две оценки, берётся лучшая:
/// подпоследовательность в духе fzf — символы образца по порядку, с бонусами за начало слова (разделители, camelCase,
/// цифры) и подряд идущие символы и со штрафом за разрывы (ловит пропуски: <c>rprt</c> → <c>report</c>);
/// приближённая подстрока — наименьшее расстояние редактирования до любого куска текста, где перестановка соседних
/// символов считается одной ошибкой (ловит опечатки: <c>reprot</c> → <c>report</c>). Работает со свёрнутым текстом,
/// границы слов берёт из исходного.
/// </summary>
public static class FuzzyMatcher
{
    private const double WordStartBonus = 2.0;
    private const double ConsecutiveBonus = 1.5;
    private const double GapPenalty = 0.15;
    private const double MaxPerCharacter = 1 + WordStartBonus + ConsecutiveBonus;
    private const int MinTypoPatternLength = 3;

    /// <summary>
    /// Оценка от 0 до 1 и позиции совпавших символов (для подсветки).
    /// </summary>
    /// <param name="pattern">Свёрнутый образец.</param>
    /// <param name="folded">Свёрнутый текст.</param>
    /// <param name="original">Исходный текст той же длины — для границ слов.</param>
    public static FuzzyMatch Match(string pattern, string folded, string original)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(folded);
        ArgumentNullException.ThrowIfNull(original);

        if (pattern.Length == 0 || folded.Length == 0)
        {
            return FuzzyMatch.None;
        }

        var subsequence = MatchSubsequence(pattern, folded, original);
        var typo = pattern.Length >= MinTypoPatternLength ? MatchWithTypos(pattern, folded) : FuzzyMatch.None;
        return subsequence.Score >= typo.Score ? subsequence : typo;
    }

    /// <summary>Триграммы строки (для запроса кандидатов).</summary>
    public static IReadOnlyList<string> Trigrams(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var result = new List<string>();
        for (var i = 0; i + 3 <= text.Length; i++)
        {
            var trigram = text.Substring(i, 3);
            if (!result.Contains(trigram))
            {
                result.Add(trigram);
            }
        }

        return result;
    }

    /// <summary>
    /// Лучшая расстановка символов образца по тексту (динамическое программирование, как fzf v2): каждый символ даёт 1,
    /// начало слова — ещё 2, символ сразу за предыдущим — ещё 1,5, разрыв — штраф до 1. Штраф за разрыв ограничен,
    /// поэтому для дальних предшественников достаточно бегущего максимума — сложность O(длина образца × длина текста).
    /// </summary>
    private static FuzzyMatch MatchSubsequence(string pattern, string folded, string original)
    {
        var m = pattern.Length;
        var n = folded.Length;
        if (m > n)
        {
            return FuzzyMatch.None;
        }

        var cappedGap = (int)Math.Ceiling(1.0 / GapPenalty);
        var score = new double[m, n];
        var from = new int[m, n];
        for (var i = 0; i < m; i++)
        {
            var farBest = double.NegativeInfinity;
            var farIndex = -1;
            for (var j = 0; j < n; j++)
            {
                score[i, j] = double.NegativeInfinity;
                from[i, j] = -1;

                // Предшественник на расстоянии не меньше cappedGap штрафуется одинаково: держим их максимум.
                if (i > 0 && j - cappedGap - 1 >= 0 && score[i - 1, j - cappedGap - 1] > farBest)
                {
                    farBest = score[i - 1, j - cappedGap - 1];
                    farIndex = j - cappedGap - 1;
                }

                if (folded[j] != pattern[i])
                {
                    continue;
                }

                var own = 1 + (SearchText.IsWordStart(original, j) ? WordStartBonus : 0);
                if (i == 0)
                {
                    score[i, j] = own;
                    continue;
                }

                var best = farIndex >= 0 ? farBest - 1.0 : double.NegativeInfinity;
                var bestIndex = farIndex;
                for (var k = Math.Max(0, j - cappedGap); k < j; k++)
                {
                    var previous = score[i - 1, k];
                    if (double.IsNegativeInfinity(previous))
                    {
                        continue;
                    }

                    var gap = j - k - 1;
                    var candidate = previous + (gap == 0 ? ConsecutiveBonus : -Math.Min(gap * GapPenalty, 1.0));
                    if (candidate > best)
                    {
                        best = candidate;
                        bestIndex = k;
                    }
                }

                if (bestIndex >= 0)
                {
                    score[i, j] = best + own;
                    from[i, j] = bestIndex;
                }
            }
        }

        var end = -1;
        var total = double.NegativeInfinity;
        for (var j = 0; j < n; j++)
        {
            if (score[m - 1, j] > total)
            {
                total = score[m - 1, j];
                end = j;
            }
        }

        if (end < 0)
        {
            return FuzzyMatch.None;
        }

        var positions = new int[m];
        for (int i = m - 1, j = end; i >= 0; j = from[i, j], i--)
        {
            positions[i] = j;
        }

        return new FuzzyMatch(Math.Clamp(total / (m * MaxPerCharacter), 0, 1), positions);
    }

    /// <summary>
    /// Приближённая подстрока (алгоритм Селлерса с перестановками, OSA): начало куска в тексте свободное, поэтому
    /// расстояние считается до лучшего куска, а не до всего имени. Сходство — 1 − расстояние / длина образца.
    /// </summary>
    private static FuzzyMatch MatchWithTypos(string pattern, string text)
    {
        var m = pattern.Length;
        var n = text.Length;
        var previous2 = new int[n + 1];
        var previous = new int[n + 1];
        var current = new int[n + 1];

        // Строка 0: пустой образец совпадает с пустым куском в любом месте текста.
        for (var i = 1; i <= m; i++)
        {
            current[0] = i;
            for (var j = 1; j <= n; j++)
            {
                var cost = pattern[i - 1] == text[j - 1] ? 0 : 1;
                var value = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
                if (i > 1 && j > 1 && pattern[i - 1] == text[j - 2] && pattern[i - 2] == text[j - 1])
                {
                    value = Math.Min(value, previous2[j - 2] + 1);
                }

                current[j] = value;
            }

            (previous2, previous, current) = (previous, current, previous2);
        }

        var best = int.MaxValue;
        var end = 0;
        for (var j = 0; j <= n; j++)
        {
            if (previous[j] < best)
            {
                best = previous[j];
                end = j;
            }
        }

        var similarity = Math.Max(0, 1 - ((double)best / m));
        if (similarity <= 0 || end == 0)
        {
            return FuzzyMatch.None;
        }

        var start = Math.Max(0, end - m);
        return new FuzzyMatch(similarity, Enumerable.Range(start, end - start).ToArray());
    }
}

/// <summary>
/// Итог нечёткого сравнения.
/// </summary>
/// <param name="Score">Оценка от 0 до 1.</param>
/// <param name="Positions">Позиции совпавших символов по возрастанию.</param>
public sealed record FuzzyMatch(double Score, IReadOnlyList<int> Positions)
{
    /// <summary>Нет совпадения.</summary>
    public static FuzzyMatch None { get; } = new(0, []);
}

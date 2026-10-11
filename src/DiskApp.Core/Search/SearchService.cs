using System.Diagnostics;

using DiskApp.Core.Abstractions;
using DiskApp.Core.Domain;
using DiskApp.Core.Paths;
using DiskApp.Core.Repositories;
using DiskApp.Core.Settings;

namespace DiskApp.Core.Search;

/// <summary>
/// Поисковый сервис (PLAN.md, этап 3.5): разбор → план → кандидаты из индекса → ранжирование → страница с подсветкой.
/// Пустой запрос даёт подборку (избранное, недавние). Если точных совпадений мало, добавляются нечёткие (опечатки).
/// Уточнение запроса (добавлен символ к терму) фильтрует прежних кандидатов в памяти, пока их набор был полным.
/// Потокобезопасен.
/// </summary>
public sealed class SearchService
{
    private static readonly TimeSpan RootsCacheTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ExtensionsCacheTtl = TimeSpan.FromMinutes(1);
    private static readonly string[] DateSuggestions = ["today", "yesterday", "week", "month", "year"];
    private static readonly string[] SizeSuggestions = [">1mb", ">10mb", ">100mb", ">1gb", "<100kb", "empty"];

    private readonly ISearchIndex _index;
    private readonly IRootRepository _roots;
    private readonly IQueryHistoryRepository _history;
    private readonly ITagRepository _tags;
    private readonly IClock _clock;
    private readonly PathNormalizer _paths;
    private volatile SearchOptions _options;
    private readonly TimeZoneInfo _zone;
    private readonly Lock _gate = new();
    private RefinementCache? _cache;
    private (DateTimeOffset At, IReadOnlyList<Root> Roots)? _rootsCache;
    private (DateTimeOffset At, IReadOnlyList<string> Extensions)? _extensionsCache;

    /// <summary>
    /// Создаёт сервис.
    /// </summary>
    public SearchService(
        ISearchIndex index,
        IRootRepository roots,
        IQueryHistoryRepository history,
        ITagRepository tags,
        IClock clock,
        PathNormalizer paths,
        SearchOptions? options = null,
        TimeZoneInfo? zone = null)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _tags = tags ?? throw new ArgumentNullException(nameof(tags));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _options = options ?? new SearchOptions();
        _zone = zone ?? TimeZoneInfo.Local;
    }

    /// <summary>
    /// Ищет и возвращает страницу результатов.
    /// </summary>
    public async Task<SearchPage> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Limit, 1);

        var stopwatch = Stopwatch.StartNew();
        var now = _clock.UtcNow;
        var query = QueryParser.Parse(request.Text);
        var roots = await GetRootsAsync(cancellationToken).ConfigureAwait(false);
        var plan = SearchPlanner.Build(query, roots, _paths, now, _zone);
        var rootPaths = roots.ToDictionary(static root => root.Id, static root => root.Path);
        var ranker = new SearchRanker(RankingWeights.For(request.Preset ?? _options.Preset), now, _options.FrecencyHalfLife);

        IReadOnlyList<SearchCandidate> candidates;
        var truncated = false;
        var partial = false;
        var fuzzyAdded = false;
        var exactCount = 0;

        using var countCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<CandidateCount>? countTask = null;
        if (plan.IsEmpty)
        {
            candidates = await _index.FindRecentAsync(Math.Max(request.Limit, _options.RecentLimit), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var refined = TryRefine(plan);
            if (refined is null)
            {
                // Подсчёт идёт параллельно выборке на другом соединении (WAL допускает параллельных читателей):
                // если выборка окажется усечённой, число уже готово или почти готово.
                countTask = _index.CountAsync(plan, _options.CountCap, countCancellation.Token);
            }

            var batch = refined ?? await _index.FindAsync(plan, new CandidateQuery(_options.CandidateLimit, _options.QueryTimeout), cancellationToken).ConfigureAwait(false);
            candidates = batch.Candidates;
            truncated = batch.Truncated;
            partial = batch.Partial;
            Remember(plan, batch);
            exactCount = candidates.Count;

            if (_options.FuzzyEnabled && plan.FtsTerms.Count > 0 && candidates.Count < _options.FuzzyThreshold && !partial)
            {
                var fuzzy = await FindFuzzyAsync(plan, candidates, cancellationToken).ConfigureAwait(false);
                if (fuzzy.Count > 0)
                {
                    candidates = [.. candidates, .. fuzzy];
                    fuzzyAdded = true;
                }
            }
        }

        var terms = plan.PositiveTerms;
        var ranked = new List<RankedCandidate>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = candidates[i];
            var fullPath = FullPath(candidate.Entry, rootPaths);
            var rankedCandidate = ranker.Rank(candidate, terms, fullPath, request.IncludeRankReason);

            // Нечёткие кандидаты остаются, только если каждый терм похож на имя достаточно сильно.
            if (i >= exactCount && fuzzyAdded && rankedCandidate.MinTermSimilarity < _options.FuzzyMinimumSimilarity)
            {
                continue;
            }

            ranked.Add(rankedCandidate);
        }

        fuzzyAdded = fuzzyAdded && ranked.Count > exactCount;

        Sort(ranked, request);
        var start = FindStart(ranked, request.Cursor);
        var pageItems = ranked.Skip(start).Take(request.Limit).ToList();
        var hits = pageItems.Select(item => ToHit(item, rootPaths)).ToList();
        SearchCursor? next = start + pageItems.Count < ranked.Count && hits.Count > 0
            ? new SearchCursor(CursorKey(request, pageItems[^1], start + pageItems.Count - 1), hits[^1].Entry.Id)
            : null;

        long total = ranked.Count;
        var exactTotal = !partial && !fuzzyAdded;
        if (truncated && !partial)
        {
            var count = await (countTask ?? _index.CountAsync(plan, _options.CountCap, cancellationToken)).ConfigureAwait(false);
            total = Math.Max(total, count.Count);
            exactTotal = count.IsExact && !fuzzyAdded;
        }
        else if (countTask is not null)
        {
            // Выборка полная — число известно и так; параллельный подсчёт больше не нужен.
            await countCancellation.CancelAsync().ConfigureAwait(false);
            await countTask.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).ConfigureAwait(false);
        }

        return new SearchPage(hits, total, next, stopwatch.Elapsed, partial)
        {
            ParsedQuery = query,
            Warnings = plan.Warnings,
            IsTotalApproximate = !exactTotal,
            IncludesFuzzy = fuzzyAdded,
        };
    }

    /// <summary>
    /// Подсказки к набираемому запросу: ключи фильтров, значения (<c>ext:</c> из индекса, <c>tag:</c> из тегов, <c>in:</c>
    /// из папок, <c>type:</c>, <c>is:</c>, даты и размеры), история. Каждая подсказка — готовая строка запроса.
    /// </summary>
    public async Task<IReadOnlyList<SearchSuggestion>> SuggestAsync(string? text, int limit = 10, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        text ??= string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            var recent = await _history.GetRecentAsync(limit, cancellationToken).ConfigureAwait(false);
            return recent.Select(static entry => new SearchSuggestion(entry.Query, entry.Query, SuggestionKind.History)).ToArray();
        }

        var suggestions = new List<SearchSuggestion>();
        var endsWithSpace = char.IsWhiteSpace(text[^1]);
        var tokens = QueryParser.Parse(text).Tokens;
        var last = endsWithSpace || tokens.Count == 0 ? null : tokens[^1];
        if (last is not null && last.End == text.Length)
        {
            var head = text[..last.Start];
            var negation = last.Text.StartsWith('-') ? "-" : string.Empty;
            var body = last.Text[negation.Length..];
            var colon = body.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && QueryParser.TryGetKey(body[..colon], out var key))
            {
                var partialValue = body[(colon + 1)..].Trim('"');
                foreach (var value in await SuggestValuesAsync(key, partialValue, limit, cancellationToken).ConfigureAwait(false))
                {
                    var quoted = value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;
                    var completion = $"{head}{negation}{QueryParser.CanonicalKey(key)}:{quoted}";
                    suggestions.Add(new SearchSuggestion(completion, $"{QueryParser.CanonicalKey(key)}:{quoted}", SuggestionKind.FilterValue));
                }
            }
            else if (last.Kind == QueryTokenKind.Term && body.Length > 0)
            {
                var folded = SearchText.Fold(body);
                foreach (var keyName in QueryParser.CanonicalKeys.Where(keyName => keyName.StartsWith(folded, StringComparison.Ordinal) && keyName != folded))
                {
                    suggestions.Add(new SearchSuggestion($"{head}{negation}{keyName}:", keyName + ":", SuggestionKind.FilterKey));
                }
            }
        }

        var history = await _history.SuggestAsync(text.Trim(), limit, cancellationToken).ConfigureAwait(false);
        foreach (var entry in history)
        {
            if (!string.Equals(entry.Query, text.Trim(), StringComparison.Ordinal))
            {
                suggestions.Add(new SearchSuggestion(entry.Query, entry.Query, SuggestionKind.History));
            }
        }

        return suggestions.DistinctBy(static suggestion => suggestion.Query).Take(limit).ToArray();
    }

    /// <summary>
    /// Запоминает выполненный запрос в истории (при открытии результата) и держит историю в пределах лимита.
    /// </summary>
    public async Task RecordQueryAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await _history.RecordAsync(text.Trim(), _clock.UtcNow, cancellationToken).ConfigureAwait(false);
        var recent = await _history.GetRecentAsync(_options.HistoryLimit + 50, cancellationToken).ConfigureAwait(false);
        foreach (var stale in recent.Skip(_options.HistoryLimit))
        {
            await _history.RemoveAsync(stale.Query, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>История запросов, свежие первыми.</summary>
    public Task<IReadOnlyList<QueryHistoryEntry>> GetHistoryAsync(int limit, CancellationToken cancellationToken = default)
        => _history.GetRecentAsync(limit, cancellationToken);

    /// <summary>Удаляет запрос из истории.</summary>
    public Task<bool> RemoveFromHistoryAsync(string query, CancellationToken cancellationToken = default)
        => _history.RemoveAsync(query, cancellationToken);

    /// <summary>Очищает историю.</summary>
    public Task ClearHistoryAsync(CancellationToken cancellationToken = default) => _history.ClearAsync(cancellationToken);

    /// <summary>
    /// Новые параметры поиска из настроек (PLAN.md, этап 9.1): действуют со следующего запроса, кэш уточнения сбрасывается.
    /// </summary>
    public void UpdateOptions(SearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        InvalidateCaches();
    }

    /// <summary>Сбрасывает кэши корней и уточнения: после изменения индекса или корней.</summary>
    public void InvalidateCaches()
    {
        lock (_gate)
        {
            _cache = null;
            _rootsCache = null;
            _extensionsCache = null;
        }
    }

    private static void Sort(List<RankedCandidate> ranked, SearchRequest request)
    {
        Comparison<RankedCandidate> tieBreak = static (left, right) =>
        {
            var byName = string.Compare(left.Candidate.Entry.Name, right.Candidate.Entry.Name, StringComparison.OrdinalIgnoreCase);
            if (byName != 0)
            {
                return byName;
            }

            var byPath = string.Compare(left.Candidate.Entry.RelPath, right.Candidate.Entry.RelPath, StringComparison.Ordinal);
            return byPath != 0 ? byPath : left.Candidate.Entry.Id.CompareTo(right.Candidate.Entry.Id);
        };

        var direction = request.Descending ? -1 : 1;
        Comparison<RankedCandidate> primary = request.Sort switch
        {
            SearchSortField.Name => (left, right) => direction * string.Compare(left.Candidate.Entry.Name, right.Candidate.Entry.Name, StringComparison.OrdinalIgnoreCase),
            SearchSortField.Modified => (left, right) => direction * left.Candidate.Entry.ModifiedUtc.CompareTo(right.Candidate.Entry.ModifiedUtc),
            SearchSortField.Created => (left, right) => direction * left.Candidate.Entry.CreatedUtc.CompareTo(right.Candidate.Entry.CreatedUtc),
            SearchSortField.Size => (left, right) => direction * left.Candidate.Entry.Size.CompareTo(right.Candidate.Entry.Size),
            _ => static (left, right) => right.Score.CompareTo(left.Score),
        };

        ranked.Sort((left, right) =>
        {
            var result = primary(left, right);
            return result != 0 ? result : tieBreak(left, right);
        });
    }

    private static int FindStart(List<RankedCandidate> ranked, SearchCursor? cursor)
    {
        if (cursor is not { } value)
        {
            return 0;
        }

        var index = ranked.FindIndex(item => item.Candidate.Entry.Id == value.EntryId);
        if (index >= 0)
        {
            return index + 1;
        }

        // Запись с курсора пропала (индекс изменился): продолжаем с первой, оценённой ниже курсора.
        var fallback = ranked.FindIndex(item => item.Score < value.Score);
        return fallback < 0 ? ranked.Count : fallback;
    }

    private static double CursorKey(SearchRequest request, RankedCandidate last, int position)
        => request.Sort == SearchSortField.Relevance ? last.Score : position;

    private static string FullPath(FileEntry entry, Dictionary<long, string> rootPaths)
        => rootPaths.TryGetValue(entry.RootId, out var rootPath) ? entry.GetFullPath(rootPath) : entry.RelPath;

    private static SearchHit ToHit(RankedCandidate item, Dictionary<long, string> rootPaths)
    {
        var candidate = item.Candidate;
        return new SearchHit(candidate.Entry, item.Score, item.NameHighlights, item.PathHighlights, item.Reason)
        {
            FullPath = FullPath(candidate.Entry, rootPaths),
            IsFavorite = candidate.IsFavorite,
            OpenCount = candidate.OpenCount,
            LastOpenedUtc = candidate.LastOpenedUtc,
            Match = item.BestMatch,
        };
    }

    private async Task<IReadOnlyList<SearchCandidate>> FindFuzzyAsync(SearchPlan plan, IReadOnlyList<SearchCandidate> exact, CancellationToken cancellationToken)
    {
        var trigrams = plan.FtsTerms.SelectMany(FuzzyMatcher.Trigrams).Distinct(StringComparer.Ordinal).ToArray();
        if (trigrams.Length == 0)
        {
            return [];
        }

        var found = await _index.FindByAnyTrigramAsync(plan, trigrams, _options.FuzzyCandidateLimit, cancellationToken).ConfigureAwait(false);
        var known = exact.Select(static candidate => candidate.Entry.Id).ToHashSet();
        return found.Where(candidate => !known.Contains(candidate.Entry.Id)).ToArray();
    }

    private CandidateBatch? TryRefine(SearchPlan plan)
    {
        RefinementCache? cache;
        lock (_gate)
        {
            cache = _cache;
        }

        if (cache is null
            || plan.FtsTerms.Count == 0
            || plan.ShortTerms.Count > 0
            || !string.Equals(cache.Signature, plan.ConditionsSignature, StringComparison.Ordinal)
            || !cache.Terms.All(old => plan.FtsTerms.Any(term => term.Contains(old, StringComparison.Ordinal))))
        {
            return null;
        }

        var refined = cache.Candidates
            .Where(candidate =>
            {
                var folded = SearchText.Fold(candidate.Entry.Name);
                return plan.FtsTerms.All(term => folded.Contains(term, StringComparison.Ordinal));
            })
            .ToArray();
        return new CandidateBatch(refined, false, false);
    }

    private void Remember(SearchPlan plan, CandidateBatch batch)
    {
        var cache = batch.Truncated || batch.Partial || plan.FtsTerms.Count == 0 || plan.ShortTerms.Count > 0
            ? null
            : new RefinementCache(plan.ConditionsSignature, plan.FtsTerms, batch.Candidates);
        lock (_gate)
        {
            _cache = cache;
        }
    }

    private async Task<IReadOnlyList<Root>> GetRootsAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        lock (_gate)
        {
            if (_rootsCache is { } cached && now - cached.At < RootsCacheTtl)
            {
                return cached.Roots;
            }
        }

        var roots = await _roots.GetAllAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _rootsCache = (now, roots);
        }

        return roots;
    }

    private async Task<IReadOnlyList<string>> SuggestValuesAsync(SearchFilterKey key, string partial, int limit, CancellationToken cancellationToken)
    {
        var folded = SearchText.Fold(partial);
        switch (key)
        {
            case SearchFilterKey.Extension:
                return (await GetExtensionsAsync(cancellationToken).ConfigureAwait(false))
                    .Where(extension => extension.StartsWith(folded.TrimStart('.'), StringComparison.Ordinal))
                    .Take(limit)
                    .ToArray();
            case SearchFilterKey.Kind:
                return FilterValues.KindSynonyms.Keys.Where(word => word.StartsWith(folded, StringComparison.Ordinal)).Order(StringComparer.Ordinal).Take(limit).ToArray();
            case SearchFilterKey.Attribute:
                return FilterValues.AttributeValues.Where(value => value.StartsWith(folded, StringComparison.Ordinal)).ToArray();
            case SearchFilterKey.Tag:
                return (await _tags.GetAllAsync(cancellationToken).ConfigureAwait(false))
                    .Select(static tag => tag.Name)
                    .Where(name => SearchText.Fold(name).StartsWith(folded, StringComparison.Ordinal))
                    .Take(limit)
                    .ToArray();
            case SearchFilterKey.Folder:
                return folded.Length == 0 ? [] : await _index.FindFolderNamesAsync(folded, limit, cancellationToken).ConfigureAwait(false);
            case SearchFilterKey.Root:
                return (await GetRootsAsync(cancellationToken).ConfigureAwait(false))
                    .Select(static root => root.Label)
                    .Where(label => SearchText.Fold(label).StartsWith(folded, StringComparison.Ordinal))
                    .Take(limit)
                    .ToArray();
            case SearchFilterKey.Modified or SearchFilterKey.Created:
                return DateSuggestions.Where(value => value.StartsWith(folded, StringComparison.Ordinal)).ToArray();
            case SearchFilterKey.Size:
                return SizeSuggestions.Where(value => value.StartsWith(folded, StringComparison.Ordinal)).ToArray();
            case SearchFilterKey.Favorite:
                return ["yes", "no"];
            default:
                return [];
        }
    }

    private async Task<IReadOnlyList<string>> GetExtensionsAsync(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        lock (_gate)
        {
            if (_extensionsCache is { } cached && now - cached.At < ExtensionsCacheTtl)
            {
                return cached.Extensions;
            }
        }

        var extensions = await _index.GetTopExtensionsAsync(string.Empty, 200, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _extensionsCache = (now, extensions);
        }

        return extensions;
    }

    private sealed record RefinementCache(string Signature, IReadOnlyList<string> Terms, IReadOnlyList<SearchCandidate> Candidates);
}

/// <summary>
/// Запрос к поисковому сервису.
/// </summary>
public sealed record SearchRequest
{
    /// <summary>Строка поиска.</summary>
    public string? Text { get; init; }

    /// <summary>Размер страницы.</summary>
    public int Limit { get; init; } = SearchQuery.DefaultLimit;

    /// <summary>Курсор следующей страницы.</summary>
    public SearchCursor? Cursor { get; init; }

    /// <summary>Пресет весов; <c>null</c> — из настроек.</summary>
    public RankingPreset? Preset { get; init; }

    /// <summary>Поле сортировки.</summary>
    public SearchSortField Sort { get; init; } = SearchSortField.Relevance;

    /// <summary>По убыванию (для релевантности не учитывается).</summary>
    public bool Descending { get; init; } = true;

    /// <summary>Добавить объяснение оценки к результатам.</summary>
    public bool IncludeRankReason { get; init; }
}

/// <summary>
/// Параметры поиска.
/// </summary>
public sealed record SearchOptions
{
    /// <summary>
    /// Наибольшее число кандидатов из индекса до ранжирования (PLAN.md, 3.2: «например 2 000»; ADR-0005 допускал до 5 000).
    /// При усечении индекс добирает начинающиеся с терма, недавно открытые и избранные, поэтому лучшие не теряются.
    /// </summary>
    public int CandidateLimit { get; init; } = 2_000;

    /// <summary>Потолок подсчёта общего числа совпадений, если он дорог; больше — «20 000+».</summary>
    public long CountCap { get; init; } = 20_000;

    /// <summary>Предел времени запроса к индексу; по истечении — частичный результат.</summary>
    public TimeSpan QueryTimeout { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Пресет весов по умолчанию.</summary>
    public RankingPreset Preset { get; init; } = RankingPreset.Balanced;

    /// <summary>Период полураспада frecency.</summary>
    public TimeSpan FrecencyHalfLife { get; init; } = TimeSpan.FromDays(30);

    /// <summary>Сколько запросов хранить в истории.</summary>
    public int HistoryLimit { get; init; } = 100;

    /// <summary>Сколько записей в подборке для пустого запроса.</summary>
    public int RecentLimit { get; init; } = 50;

    /// <summary>Нечёткий поиск включён (настройка <c>search.fuzzy</c>).</summary>
    public bool FuzzyEnabled { get; init; } = true;

    /// <summary>Если точных совпадений меньше, добавляются нечёткие.</summary>
    public int FuzzyThreshold { get; init; } = 5;

    /// <summary>Кандидатов для нечёткого поиска.</summary>
    public int FuzzyCandidateLimit { get; init; } = 2_000;

    /// <summary>
    /// Наименьшее сходство каждого терма с именем для нечёткого кандидата: 1 − ошибок / длина терма. 0,8 — одна ошибка
    /// (перестановка соседних букв — тоже одна) на пять символов: «reprot» находит «report», а «repo» не тянет «reptile».
    /// </summary>
    public double FuzzyMinimumSimilarity { get; init; } = 0.8;

    /// <summary>
    /// Параметры из настроек поиска. Лимит кандидатов ограничен 100…5 000 (ADR-0005): больше — вне бюджета 50 мс.
    /// </summary>
    public static SearchOptions FromSettings(SearchSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var defaults = new SearchOptions();
        return defaults with
        {
            CandidateLimit = settings.CandidateLimit > 0 ? Math.Clamp(settings.CandidateLimit, 100, 5_000) : defaults.CandidateLimit,
            HistoryLimit = settings.HistoryLimit > 0 ? settings.HistoryLimit : defaults.HistoryLimit,
            FrecencyHalfLife = settings.FrecencyHalfLifeDays > 0 ? TimeSpan.FromDays(settings.FrecencyHalfLifeDays) : defaults.FrecencyHalfLife,
            Preset = RankingWeights.ParsePreset(settings.RankingPreset),
            FuzzyEnabled = settings.Fuzzy,
        };
    }
}

/// <summary>
/// Подсказка к запросу.
/// </summary>
/// <param name="Query">Полная строка запроса после выбора подсказки.</param>
/// <param name="Display">Что показать в списке.</param>
/// <param name="Kind">Вид подсказки.</param>
public sealed record SearchSuggestion(string Query, string Display, SuggestionKind Kind);

/// <summary>
/// Вид подсказки.
/// </summary>
public enum SuggestionKind
{
    /// <summary>Ключ фильтра.</summary>
    FilterKey,

    /// <summary>Значение фильтра.</summary>
    FilterValue,

    /// <summary>Запрос из истории.</summary>
    History,
}

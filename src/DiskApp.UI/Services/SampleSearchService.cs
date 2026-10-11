using System.Diagnostics;

using DiskApp.UI.Models;

namespace DiskApp.UI.Services;

/// <summary>
/// Поиск по макетному набору файлов в памяти. Повторяет поведение будущего индекса настолько, чтобы интерфейс
/// можно было проектировать и проверять: подстрока без учёта регистра по всем словам запроса, ранжирование
/// по началу имени, избранному и свежести, недавние файлы с учётом открытий.
/// </summary>
public sealed class SampleSearchService : ISearchService
{
    private const int PageSize = 50;
    private const int RecentLimit = 8;

    private static readonly string[] FilterKeys = ["ext", "type", "in", "root", "size", "modified", "created", "is", "tag", "fav"];

    private readonly IReadOnlyList<FileEntry> _entries;
    private readonly List<FileEntry> _recent;
    private readonly List<string> _history = ["отчёт", "бюджет", "договор"];
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    /// <summary>
    /// Создаёт сервис с набором <see cref="SampleData"/> относительно текущего времени.
    /// </summary>
    public SampleSearchService()
        : this(static () => DateTimeOffset.Now)
    {
    }

    /// <summary>
    /// Создаёт сервис с набором <see cref="SampleData"/> и заданными часами (для тестов).
    /// </summary>
    public SampleSearchService(Func<DateTimeOffset> clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        _clock = clock;
        var now = clock();
        _entries = SampleData.CreateEntries(now);
        _recent = SampleData.CreateRecent(_entries);
    }

    /// <inheritdoc />
    public Task<SearchResult> SearchAsync(string query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return Task.FromResult(SearchResult.Empty);
        }

        var stopwatch = Stopwatch.StartNew();
        var now = _clock();
        List<FileEntry> recentSnapshot;
        lock (_gate)
        {
            recentSnapshot = [.. _recent];
        }

        var ranked = _entries
            .Select(entry => (Entry: entry, Score: Score(entry, tokens, recentSnapshot, now)))
            .Where(static item => item.Score > 0)
            .OrderByDescending(static item => item.Score)
            .ThenBy(static item => item.Entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(static item => item.Entry)
            .ToList();

        stopwatch.Stop();
        var page = ranked.Count > PageSize ? ranked.GetRange(0, PageSize) : ranked;
        return Task.FromResult(new SearchResult(page, ranked.Count, stopwatch.Elapsed));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FileEntry>> GetRecentAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<FileEntry>>([.. _recent.Take(RecentLimit)]);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ResultSection>> GetSuggestionsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<FileEntry> recent;
        lock (_gate)
        {
            recent = [.. _recent.Take(RecentLimit)];
        }

        ResultSection[] sections =
        [
            new(SectionTitles.RecentlyOpened, recent),
            new(SectionTitles.Pinned, [.. _entries.Where(static entry => entry.IsFavorite)]),
            new(SectionTitles.RecentlyModified, [.. _entries
                .Where(static entry => entry.Kind != FileKind.Folder)
                .OrderByDescending(static entry => entry.Modified)
                .Take(RecentLimit)]),
        ];
        return Task.FromResult<IReadOnlyList<ResultSection>>([.. sections.Where(static section => section.Entries.Count > 0)]);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetHistoryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<string>>([.. _history]);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FileEntry>> GetChangedSinceAsync(TimeSpan period, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var since = _clock() - period;
        return Task.FromResult<IReadOnlyList<FileEntry>>([.. _entries
            .Where(entry => entry.Kind != FileKind.Folder && entry.Modified >= since)
            .OrderByDescending(static entry => entry.Modified)]);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<QuerySuggestion>> SuggestAsync(string query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        List<string> history;
        lock (_gate)
        {
            history = [.. _history];
        }

        if (query.Trim().Length == 0)
        {
            return Task.FromResult<IReadOnlyList<QuerySuggestion>>([.. history.Take(8).Select(static item => new QuerySuggestion(item, item, QuerySuggestionKind.History))]);
        }

        // Макет дополняет только расширения и ключи фильтров — этого хватает, чтобы проверить интерфейс.
        var suggestions = new List<QuerySuggestion>();
        var last = query.EndsWith(' ') ? string.Empty : query.Split(' ')[^1];
        var head = query[..^last.Length];
        if (last.StartsWith("ext:", StringComparison.OrdinalIgnoreCase))
        {
            var partial = last[4..];
            suggestions.AddRange(_entries
                .Select(static entry => entry.Extension)
                .Where(extension => extension.Length > 0 && extension.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .Order(StringComparer.Ordinal)
                .Take(8)
                .Select(extension => new QuerySuggestion($"{head}ext:{extension}", "ext:" + extension, QuerySuggestionKind.FilterValue)));
        }
        else if (last.Length > 0 && !last.Contains(':', StringComparison.Ordinal))
        {
            suggestions.AddRange(FilterKeys
                .Where(key => key.StartsWith(last, StringComparison.OrdinalIgnoreCase) && key.Length > last.Length)
                .Select(key => new QuerySuggestion($"{head}{key}:", key + ":", QuerySuggestionKind.FilterKey)));
        }

        return Task.FromResult<IReadOnlyList<QuerySuggestion>>(suggestions);
    }

    /// <inheritdoc />
    public Task DeleteHistoryAsync(string query, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _history.RemoveAll(item => string.Equals(item, query, StringComparison.OrdinalIgnoreCase));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ClearHistoryAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _history.Clear();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IndexStatus> GetIndexStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SampleData.CreateIndexStatus(_entries, _clock()));
    }

    /// <inheritdoc />
    public Task OpenAsync(FileEntry entry, string? query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _recent.RemoveAll(item => string.Equals(item.FullPath, entry.FullPath, StringComparison.OrdinalIgnoreCase));
            _recent.Insert(0, entry);

            if (query is { Length: > 0 })
            {
                _history.RemoveAll(item => string.Equals(item, query, StringComparison.OrdinalIgnoreCase));
                _history.Insert(0, query);
            }
        }

        return Task.CompletedTask;
    }

    private static double Score(FileEntry entry, string[] tokens, List<FileEntry> recent, DateTimeOffset now)
    {
        var name = entry.Name;
        double score = 0;

        foreach (var token in tokens)
        {
            var index = name.IndexOf(token, StringComparison.CurrentCultureIgnoreCase);
            if (index < 0)
            {
                // Слово не найдено в имени — пробуем путь, но с меньшим весом.
                if (entry.Directory.Contains(token, StringComparison.CurrentCultureIgnoreCase))
                {
                    score += 0.5;
                    continue;
                }

                return 0;
            }

            score += index == 0 ? 3 : IsWordStart(name, index) ? 2 : 1;
        }

        if (entry.IsFavorite)
        {
            score += 0.5;
        }

        var recentIndex = recent.FindIndex(item => string.Equals(item.FullPath, entry.FullPath, StringComparison.OrdinalIgnoreCase));
        if (recentIndex >= 0)
        {
            score += 1.0 - (recentIndex * 0.1);
        }

        var age = now - entry.Modified;
        if (age < TimeSpan.FromDays(7))
        {
            score += 1 - (age.TotalDays / 7);
        }

        return score;
    }

    private static bool IsWordStart(string text, int index)
    {
        var previous = text[index - 1];
        return !char.IsLetterOrDigit(previous);
    }
}

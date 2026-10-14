using System.IO.Abstractions;

using DiskApp.Core.Abstractions;
using DiskApp.Core.Operations;
using DiskApp.Core.Paths;
using DiskApp.Core.Repositories;
using DiskApp.Core.Settings;

namespace DiskApp.Core.Sorting;

/// <summary>Почему предложена папка (PLAN.md, этап 17.4).</summary>
public enum SortingSuggestionReason
{
    /// <summary>Туда уже отправляли файлы с этим словом в имени.</summary>
    Word = 1,

    /// <summary>Туда уже отправляли файлы с этим расширением.</summary>
    Extension = 2,

    /// <summary>Туда недавно переносили из этого накопителя.</summary>
    Recent = 3,
}

/// <summary>Подсказка рабочей папки по истории решений.</summary>
/// <param name="Destination">Куда: нормализованный путь папки.</param>
/// <param name="Reason">Почему.</param>
/// <param name="Count">Сколько файлов с этим признаком уже отправлено туда; для «недавно» — сколько переносов из накопителя.</param>
/// <param name="Feature">Слово, расширение без точки или, для «недавно», путь накопителя.</param>
public sealed record SortingSuggestion(string Destination, SortingSuggestionReason Reason, int Count, string Feature);

/// <summary>Счётчик решений: сколько файлов с признаком отправлено в папку.</summary>
/// <param name="Feature">Признак: <c>w:слово</c>, <c>e:расширение</c>, <c>i:путь накопителя</c>.</param>
/// <param name="Destination">Нормализованный путь папки; пусто — «оставить здесь».</param>
/// <param name="Score">Вес с затуханием на момент <paramref name="UpdatedUtc"/>.</param>
/// <param name="Count">Сколько решений всего.</param>
/// <param name="UpdatedUtc">Последнее решение.</param>
public sealed record SortingStat(string Feature, string Destination, double Score, int Count, DateTimeOffset UpdatedUtc);

/// <summary>
/// Подсказки рабочей папки по истории решений (PLAN.md, этап 17.4; решение 18, ADR-0012): счётчики «признак имени → папка» с
/// затуханием по времени, без ML. Каждый перенос с «Разбора» добавляет решение по словам имени, расширению и накопителю;
/// «оставить здесь» — решение «никуда», чтобы похожие файлы не предлагалось переносить настойчиво. Подсказка — папка с
/// наибольшей суммой долей по словам (и половинным весом по расширению), если её подтверждают хотя бы два файла с тем же
/// словом или три с тем же расширением; иначе — папка, куда недавно переносили из того же накопителя. Шаблоны рабочих
/// папок (17.5) сильнее истории — их проверяет вызывающий. Счётчики держатся в памяти, в базу пишутся изменённые.
/// </summary>
public sealed class SortingHistory(ISortingStatsRepository repository, ISettingsStore<AppSettings> settings, IClock clock, PathNormalizer paths, IFileSystem fileSystem) : IDisposable
{
    /// <summary>Решение «оставить здесь»: папка назначения в счётчике.</summary>
    public const string KeepHere = "";

    // Половинный вес расширения: «.pdf» говорит о папке меньше, чем слово «акт».
    private const double ExtensionWeight = 0.5;

    // Почти забытые решения (единица через ~1,5 года) удаляются при загрузке.
    private const double PruneBelow = 0.02;

    private const string WordPrefix = "w:";
    private const string ExtensionPrefix = "e:";
    private const string InboxPrefix = "i:";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, Dictionary<string, SortingStat>>? _stats;

    /// <summary>Через сколько вес решения уменьшается вдвое.</summary>
    public static TimeSpan HalfLife { get; } = TimeSpan.FromDays(90);

    /// <summary>Сколько перенос из накопителя считается недавним.</summary>
    public static TimeSpan RecentWindow { get; } = TimeSpan.FromDays(3);

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();

    /// <summary>Вес решения на момент <paramref name="at"/>.</summary>
    public static double Decayed(SortingStat stat, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(stat);
        var age = Math.Max(0, (at - stat.UpdatedUtc).TotalDays);
        return stat.Score * Math.Pow(0.5, age / HalfLife.TotalDays);
    }

    /// <summary>Запоминает переносы, сделанные пользователем: откуда, куда и под каким именем.</summary>
    public async Task RecordMovesAsync(IReadOnlyList<MovedItem> moves, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moves);

        var inboxes = Inboxes();
        var decisions = new List<(IReadOnlyList<string> Features, string Destination)>(moves.Count);
        foreach (var move in moves)
        {
            if (paths.TryNormalize(move.From, out var from) && paths.TryNormalize(move.To, out var to) && paths.GetParent(to) is { } destination)
            {
                var inbox = inboxes.Where(candidate => paths.IsNestedIn(from, candidate)).MaxBy(static candidate => candidate.Length);
                decisions.Add((Features(paths.GetFileName(from), fileSystem.Directory.Exists(to), inbox), destination));
            }
        }

        await RecordAsync(decisions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Запоминает «оставить здесь»: похожие файлы предлагать переносить не так настойчиво.</summary>
    public async Task RecordKeptAsync(IReadOnlyList<string> fullPaths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fullPaths);

        var decisions = new List<(IReadOnlyList<string> Features, string Destination)>(fullPaths.Count);
        foreach (var path in fullPaths)
        {
            if (paths.TryNormalize(path, out var normalized))
            {
                decisions.Add((Features(paths.GetFileName(normalized), fileSystem.Directory.Exists(normalized), inbox: null), KeepHere));
            }
        }

        await RecordAsync(decisions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Подсказки для элементов очереди накопителей по порядку; <c>null</c> — подсказать нечего.</summary>
    public async Task<IReadOnlyList<SortingSuggestion?>> SuggestAsync(IReadOnlyList<InboxItem> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stats = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var now = clock.UtcNow;
            var exists = new Dictionary<string, bool>(paths.Comparer);
            bool Exists(string folder)
            {
                if (!exists.TryGetValue(folder, out var found))
                {
                    found = fileSystem.Directory.Exists(folder);
                    exists[folder] = found;
                }

                return found;
            }

            return [.. items.Select(item => Suggest(stats, item, now, Exists))];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Пути папок в истории следуют за переименованием и переносом папки (как пути рабочих папок, этап 17.2).
    /// </summary>
    /// <param name="moves">Пары «прежний путь → новый путь».</param>
    /// <param name="cancellationToken">Отмена.</param>
    public async Task MovePathsAsync(IReadOnlyList<(string From, string To)> moves, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moves);
        if (moves.Count == 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stats = await LoadAsync(cancellationToken).ConfigureAwait(false);
            string Map(string path) => moves.Aggregate(path, (current, move) => SortingFolderTracker.MapPath(current, move.From, move.To, paths)!);

            var upserts = new Dictionary<(string Feature, string Destination), SortingStat>();
            var deletes = new List<(string Feature, string Destination)>();
            foreach (var (feature, byDestination) in stats.ToArray())
            {
                var mappedFeature = feature.StartsWith(InboxPrefix, StringComparison.Ordinal) ? InboxFeature(Map(feature[InboxPrefix.Length..])) : feature;
                foreach (var stat in byDestination.Values.ToArray())
                {
                    var mapped = stat.Destination.Length == 0 ? stat.Destination : Map(stat.Destination);
                    if (string.Equals(mappedFeature, feature, StringComparison.Ordinal) && string.Equals(mapped, stat.Destination, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    byDestination.Remove(stat.Destination);
                    deletes.Add((feature, stat.Destination));
                    var target = Bucket(stats, mappedFeature);
                    var moved = target.TryGetValue(mapped, out var existing)
                        ? Merge(existing, stat)
                        : stat with { Feature = mappedFeature, Destination = mapped };
                    target[moved.Destination] = moved;
                    upserts[(mappedFeature, moved.Destination)] = moved;
                }

                if (byDestination.Count == 0)
                {
                    stats.Remove(feature);
                }
            }

            if (deletes.Count > 0)
            {
                await repository.SaveAsync([.. upserts.Values], deletes, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // Счётчики одного признака по папкам; папки сравниваются по правилам путей.
    private Dictionary<string, SortingStat> Bucket(Dictionary<string, Dictionary<string, SortingStat>> stats, string feature)
    {
        if (!stats.TryGetValue(feature, out var bucket))
        {
            bucket = new Dictionary<string, SortingStat>(paths.Comparer);
            stats[feature] = bucket;
        }

        return bucket;
    }

    // Два счётчика одной папки сливаются: веса приводятся к более позднему решению.
    private static SortingStat Merge(SortingStat into, SortingStat other)
    {
        var updated = into.UpdatedUtc >= other.UpdatedUtc ? into.UpdatedUtc : other.UpdatedUtc;
        return into with { Score = Decayed(into, updated) + Decayed(other, updated), Count = into.Count + other.Count, UpdatedUtc = updated };
    }

    private SortingSuggestion? Suggest(Dictionary<string, Dictionary<string, SortingStat>> stats, InboxItem item, DateTimeOffset now, Func<string, bool> exists)
    {
        var parent = paths.GetParent(item.FullPath);

        // Папка, где файл уже лежит или которая внутри него самого, и пропавшая папка не предлагаются.
        bool Usable(string destination)
            => destination.Length > 0
                && (parent is null || !paths.Comparer.Equals(destination, parent))
                && !paths.IsNestedIn(item.FullPath, destination)
                && !paths.IsNestedIn(destination, item.FullPath)
                && exists(destination);

        var name = item.Entry.Name;
        var isFolder = item.Entry.IsDirectory;
        var words = SortingFeatures.Words(name, isFolder);
        var extension = SortingFeatures.Extension(name, isFolder);
        var totals = new Dictionary<string, double>(paths.Comparer);
        void Add(string feature, double weight)
        {
            if (stats.TryGetValue(feature, out var byDestination))
            {
                var total = byDestination.Values.Sum(stat => Decayed(stat, now)) + 1;
                foreach (var stat in byDestination.Values)
                {
                    totals[stat.Destination] = totals.GetValueOrDefault(stat.Destination) + (weight * Decayed(stat, now) / total);
                }
            }
        }

        foreach (var word in words)
        {
            Add(WordPrefix + word, 1);
        }

        if (extension.Length > 0)
        {
            Add(ExtensionPrefix + extension, ExtensionWeight);
        }

        var best = totals
            .Where(pair => pair.Key.Length == 0 || Usable(pair.Key))
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => pair.Key)
            .FirstOrDefault();
        if (best is { Length: 0 })
        {
            // Похожие файлы чаще оставляют на месте — не предлагать ничего, даже недавнюю папку.
            return null;
        }

        if (best is not null && Explain(stats, best, words, extension, now) is { } explained)
        {
            return explained;
        }

        var inbox = InboxFeature(paths.TryNormalize(item.Inbox, out var normalizedInbox) ? normalizedInbox : item.Inbox);
        var recent = stats.TryGetValue(inbox, out var fromInbox)
            ? fromInbox.Values
                .Where(stat => now - stat.UpdatedUtc <= RecentWindow && Usable(stat.Destination))
                .OrderByDescending(static stat => stat.UpdatedUtc)
                .FirstOrDefault()
            : null;
        return recent is null ? null : new SortingSuggestion(recent.Destination, SortingSuggestionReason.Recent, recent.Count, item.Inbox);
    }

    // Объяснение — самое весомое слово с хотя бы двумя файлами там и долей от половины, иначе расширение с тремя файлами и
    // долей от 60 %. Без объяснения подсказки нет: её нельзя показать честно.
    private static SortingSuggestion? Explain(Dictionary<string, Dictionary<string, SortingStat>> stats, string destination, IReadOnlyList<string> words, string extension, DateTimeOffset now)
    {
        (SortingStat Stat, double Share)? Support(string feature)
        {
            if (!stats.TryGetValue(feature, out var byDestination) || !byDestination.TryGetValue(destination, out var stat))
            {
                return null;
            }

            var total = byDestination.Values.Sum(other => Decayed(other, now)) + 1;
            return (stat, Decayed(stat, now) / total);
        }

        SortingSuggestion? byWord = null;
        foreach (var word in words)
        {
            if (Support(WordPrefix + word) is { } support && support.Stat.Count >= 2 && support.Share >= 0.5 && (byWord is null || support.Stat.Count > byWord.Count))
            {
                byWord = new SortingSuggestion(destination, SortingSuggestionReason.Word, support.Stat.Count, word);
            }
        }

        if (byWord is not null)
        {
            return byWord;
        }

        return extension.Length > 0 && Support(ExtensionPrefix + extension) is { } byExtension && byExtension.Stat.Count >= 3 && byExtension.Share >= 0.6
            ? new SortingSuggestion(destination, SortingSuggestionReason.Extension, byExtension.Stat.Count, extension)
            : null;
    }

    private async Task RecordAsync(List<(IReadOnlyList<string> Features, string Destination)> decisions, CancellationToken cancellationToken)
    {
        if (decisions.Count == 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stats = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var now = clock.UtcNow;
            var changed = new Dictionary<(string Feature, string Destination), SortingStat>();
            foreach (var (features, destination) in decisions)
            {
                foreach (var feature in features)
                {
                    var bucket = Bucket(stats, feature);
                    var stat = bucket.TryGetValue(destination, out var old)
                        ? old with { Score = Decayed(old, now) + 1, Count = old.Count + 1, UpdatedUtc = now }
                        : new SortingStat(feature, destination, 1, 1, now);
                    bucket[stat.Destination] = stat;
                    changed[(feature, stat.Destination)] = stat;
                }
            }

            await repository.SaveAsync([.. changed.Values], [], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Счётчики из базы — один раз; почти забытые удаляются.
    private async Task<Dictionary<string, Dictionary<string, SortingStat>>> LoadAsync(CancellationToken cancellationToken)
    {
        if (_stats is { } loaded)
        {
            return loaded;
        }

        var now = clock.UtcNow;
        var stats = new Dictionary<string, Dictionary<string, SortingStat>>(StringComparer.Ordinal);
        var stale = new List<(string Feature, string Destination)>();
        foreach (var stat in await repository.GetAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (Decayed(stat, now) < PruneBelow)
            {
                stale.Add((stat.Feature, stat.Destination));
                continue;
            }

            var bucket = Bucket(stats, stat.Feature);
            bucket[stat.Destination] = bucket.TryGetValue(stat.Destination, out var same) ? Merge(same, stat) : stat;
        }

        if (stale.Count > 0)
        {
            await repository.SaveAsync([], stale, cancellationToken).ConfigureAwait(false);
        }

        _stats = stats;
        return stats;
    }

    private List<string> Inboxes()
        => [.. settings.Current.Sorting.Inboxes.Select(inbox => paths.TryNormalize(inbox.Path, out var normalized) ? normalized : null).OfType<string>()];

    private List<string> Features(string name, bool isFolder, string? inbox)
    {
        var features = SortingFeatures.Words(name, isFolder).Select(static word => WordPrefix + word).ToList();
        var extension = SortingFeatures.Extension(name, isFolder);
        if (extension.Length > 0)
        {
            features.Add(ExtensionPrefix + extension);
        }

        if (inbox is not null)
        {
            features.Add(InboxFeature(inbox));
        }

        return features;
    }

    // Накопитель — по пути; в Windows регистр пути не важен, поэтому ключ в нижнем регистре.
    private string InboxFeature(string inbox)
        => InboxPrefix + (paths.Comparison == StringComparison.OrdinalIgnoreCase ? inbox.ToLowerInvariant() : inbox);
}

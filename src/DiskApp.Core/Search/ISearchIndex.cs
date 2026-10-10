using DiskApp.Core.Domain;

namespace DiskApp.Core.Search;

/// <summary>
/// Выборка кандидатов из индекса по плану запроса (PLAN.md, этап 3.2). Ранжирование и подсветка — в <c>SearchService</c>.
/// </summary>
public interface ISearchIndex
{
    /// <summary>
    /// Кандидаты по плану, не больше <see cref="CandidateQuery.Limit"/>. Если совпадений больше, к выборке добавляются
    /// записи с именем, начинающимся с первого терма, и недавно открытые или избранные — их ранжирование поднимает выше всего.
    /// Долгий запрос прерывается по таймауту с частичным результатом; отмена прерывает выполнение в SQLite.
    /// </summary>
    Task<CandidateBatch> FindAsync(SearchPlan plan, CandidateQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Сколько записей подходит под план. Если подсчёт дорог (фильтры поверх сотен тысяч совпадений), считается не больше
    /// <paramref name="cap"/> — тогда <see cref="CandidateCount.IsExact"/> ложно и интерфейс пишет «20 000+».
    /// </summary>
    Task<CandidateCount> CountAsync(SearchPlan plan, long cap, CancellationToken cancellationToken = default);

    /// <summary>
    /// Подборка для пустого запроса: избранное и недавно открытые, затем недавно изменённые файлы.
    /// </summary>
    Task<IReadOnlyList<SearchCandidate>> FindRecentAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Кандидаты для нечёткого поиска: записи, в имени которых есть хотя бы одна из триграмм, с условиями плана.
    /// </summary>
    Task<IReadOnlyList<SearchCandidate>> FindByAnyTrigramAsync(SearchPlan plan, IReadOnlyList<string> trigrams, int limit, CancellationToken cancellationToken = default);

    /// <summary>Самые частые расширения файлов, начинающиеся с префикса, по убыванию числа файлов.</summary>
    Task<IReadOnlyList<string>> GetTopExtensionsAsync(string prefix, int limit, CancellationToken cancellationToken = default);

    /// <summary>Имена папок, начинающиеся с префикса (без учёта регистра).</summary>
    Task<IReadOnlyList<string>> FindFolderNamesAsync(string prefix, int limit, CancellationToken cancellationToken = default);
}

/// <summary>
/// Параметры выборки кандидатов.
/// </summary>
/// <param name="Limit">Наибольшее число основных кандидатов.</param>
/// <param name="Timeout">Предел времени запроса; по истечении — частичный результат.</param>
public sealed record CandidateQuery(int Limit, TimeSpan Timeout);

/// <summary>
/// Кандидаты поиска.
/// </summary>
/// <param name="Candidates">Записи с данными для ранжирования.</param>
/// <param name="Truncated">Совпадений больше лимита: выборка неполная, общее число нужно считать отдельно.</param>
/// <param name="Partial">Запрос прерван по таймауту.</param>
public sealed record CandidateBatch(IReadOnlyList<SearchCandidate> Candidates, bool Truncated, bool Partial)
{
    /// <summary>Пустая выборка.</summary>
    public static CandidateBatch Empty { get; } = new([], false, false);
}

/// <summary>
/// Итог подсчёта совпадений.
/// </summary>
/// <param name="Count">Число совпадений или потолок подсчёта.</param>
/// <param name="IsExact">Подсчитано точно; иначе совпадений не меньше <see cref="Count"/>.</param>
public readonly record struct CandidateCount(long Count, bool IsExact);

/// <summary>
/// Запись-кандидат с данными для ранжирования.
/// </summary>
/// <param name="Entry">Запись индекса.</param>
/// <param name="OpenCount">Сколько раз открывали.</param>
/// <param name="LastOpenedUtc">Когда открывали последний раз.</param>
/// <param name="IsFavorite">В избранном.</param>
public sealed record SearchCandidate(FileEntry Entry, int OpenCount, DateTimeOffset? LastOpenedUtc, bool IsFavorite);

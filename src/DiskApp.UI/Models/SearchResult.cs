namespace DiskApp.UI.Models;

/// <summary>
/// Ответ на поисковый запрос: найденные записи в порядке ранжирования, их общее число и затраченное время.
/// </summary>
/// <param name="Entries">Записи первой страницы в порядке убывания релевантности.</param>
/// <param name="TotalCount">Сколько записей подошло всего, включая не вошедшие в первую страницу.</param>
/// <param name="Elapsed">Время поиска.</param>
/// <param name="IsTotalApproximate">Общее число приблизительное: «20 000+», поиск прерван или есть нечёткие совпадения.</param>
public sealed record SearchResult(IReadOnlyList<FileEntry> Entries, int TotalCount, TimeSpan Elapsed, bool IsTotalApproximate = false)
{
    /// <summary>Пустой ответ.</summary>
    public static SearchResult Empty { get; } = new([], 0, TimeSpan.Zero);
}

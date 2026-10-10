namespace DiskApp.Core.Search;

/// <summary>
/// Страница результатов поиска.
/// </summary>
/// <param name="Hits">Результаты в порядке ранжирования.</param>
/// <param name="TotalCount">Сколько записей подошло всего (приблизительно для больших выборок).</param>
/// <param name="NextCursor">Курсор следующей страницы; <c>null</c>, если результаты закончились.</param>
/// <param name="Elapsed">Время выполнения.</param>
/// <param name="IsPartial">Поиск прерван по таймауту или отмене, результаты неполные.</param>
public sealed record SearchPage(
    IReadOnlyList<SearchHit> Hits,
    long TotalCount,
    SearchCursor? NextCursor,
    TimeSpan Elapsed,
    bool IsPartial = false)
{
    /// <summary>Пустая страница.</summary>
    public static SearchPage Empty { get; } = new([], 0, null, TimeSpan.Zero);

    /// <summary>Есть ли следующая страница.</summary>
    public bool HasMore => NextCursor is not null;
}

namespace DiskApp.Core.Search;

/// <summary>
/// Страница результатов поиска (PLAN.md, этап 3.5).
/// </summary>
/// <param name="Hits">Результаты в порядке ранжирования.</param>
/// <param name="TotalCount">Сколько записей подошло всего.</param>
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

    /// <summary>Разобранный запрос: для подсветки синтаксиса и отладки.</summary>
    public SearchQuery? ParsedQuery { get; init; }

    /// <summary>Замечания: непонятые фильтры, неизвестные корни.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Общее число приблизительное (поиск прерван или включены нечёткие совпадения).</summary>
    public bool IsTotalApproximate { get; init; }

    /// <summary>Добавлены нечёткие совпадения: точных нашлось мало.</summary>
    public bool IncludesFuzzy { get; init; }
}

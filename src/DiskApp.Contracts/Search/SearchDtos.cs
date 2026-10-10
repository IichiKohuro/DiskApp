using DiskApp.Contracts.Entries;

namespace DiskApp.Contracts.Search;

/// <summary>
/// Запрос поиска: тело <c>POST /api/v1/search</c>; те же поля — параметры <c>GET</c> (<c>q</c>, <c>limit</c>, <c>cursor</c>,
/// <c>sort</c>, <c>desc</c>, <c>preset</c>, <c>explain</c>).
/// </summary>
public sealed record SearchRequest
{
    /// <summary>Строка запроса с фильтрами.</summary>
    public string? Q { get; set; }

    /// <summary>Размер страницы (1…500).</summary>
    public int Limit { get; set; } = 50;

    /// <summary>Курсор следующей страницы из прошлого ответа.</summary>
    public string? Cursor { get; set; }

    /// <summary>Сортировка.</summary>
    public SearchSort Sort { get; set; } = SearchSort.Relevance;

    /// <summary>По убыванию (для релевантности не учитывается).</summary>
    public bool Descending { get; set; } = true;

    /// <summary>Пресет ранжирования; <c>null</c> — из настроек.</summary>
    public RankingPreset? Preset { get; set; }

    /// <summary>Добавить объяснение оценки к результатам.</summary>
    public bool Explain { get; set; }
}

/// <summary>Поле сортировки.</summary>
public enum SearchSort
{
    /// <summary>По релевантности.</summary>
    Relevance,

    /// <summary>По имени.</summary>
    Name,

    /// <summary>По времени изменения.</summary>
    Modified,

    /// <summary>По времени создания.</summary>
    Created,

    /// <summary>По размеру.</summary>
    Size,
}

/// <summary>Пресет весов ранжирования.</summary>
public enum RankingPreset
{
    /// <summary>Сбалансированно.</summary>
    Balanced,

    /// <summary>По имени.</summary>
    ByName,

    /// <summary>Недавнее.</summary>
    Recent,
}

/// <summary>
/// Страница результатов поиска.
/// </summary>
/// <param name="Items">Записи с оценкой и подсветкой.</param>
/// <param name="Total">Сколько всего совпадений или потолок подсчёта.</param>
/// <param name="IsTotalApproximate">Общее число приблизительное: «20 000+», поиск прерван или есть нечёткие совпадения.</param>
/// <param name="NextCursor">Курсор следующей страницы; <c>null</c> — страниц больше нет.</param>
/// <param name="ElapsedMs">Время поиска на сервере.</param>
/// <param name="IsPartial">Поиск прерван по таймауту: результат неполный.</param>
/// <param name="IncludesFuzzy">Добавлены нечёткие совпадения.</param>
/// <param name="Warnings">Замечания к запросу: непонятые фильтры, неизвестные корни.</param>
/// <param name="Tokens">Разбор запроса для подсветки синтаксиса.</param>
public sealed record SearchResponse(
    IReadOnlyList<EntryDto> Items,
    long Total,
    bool IsTotalApproximate,
    string? NextCursor,
    double ElapsedMs,
    bool IsPartial,
    bool IncludesFuzzy,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<QueryTokenDto> Tokens);

/// <summary>
/// Токен разобранного запроса.
/// </summary>
/// <param name="Kind">Вид.</param>
/// <param name="Start">Начало в строке запроса.</param>
/// <param name="Length">Длина.</param>
/// <param name="Text">Текст токена.</param>
public sealed record QueryTokenDto(QueryTokenKind Kind, int Start, int Length, string Text);

/// <summary>Вид токена запроса.</summary>
public enum QueryTokenKind
{
    /// <summary>Слово.</summary>
    Term,

    /// <summary>Фраза в кавычках.</summary>
    Phrase,

    /// <summary>Фильтр <c>ключ:значение</c>.</summary>
    Filter,

    /// <summary>Фильтр без значения или с непонятым значением.</summary>
    IncompleteFilter,

    /// <summary>Регулярное выражение.</summary>
    Regex,
}

/// <summary>
/// Подсказки: <c>GET /api/v1/suggest?q=</c>.
/// </summary>
/// <param name="Items">Подсказки по порядку.</param>
public sealed record SuggestResponse(IReadOnlyList<SuggestionDto> Items);

/// <summary>
/// Подсказка.
/// </summary>
/// <param name="Query">Полная строка запроса после выбора.</param>
/// <param name="Display">Что показать.</param>
/// <param name="Kind">Вид.</param>
public sealed record SuggestionDto(string Query, string Display, SuggestionKind Kind);

/// <summary>Вид подсказки.</summary>
public enum SuggestionKind
{
    /// <summary>Ключ фильтра.</summary>
    FilterKey,

    /// <summary>Значение фильтра.</summary>
    FilterValue,

    /// <summary>Запрос из истории.</summary>
    History,
}

/// <summary>
/// Запись истории запросов: <c>GET /api/v1/history</c>.
/// </summary>
/// <param name="Query">Запрос.</param>
/// <param name="UseCount">Сколько раз использован.</param>
/// <param name="LastUsedUtc">Когда последний раз.</param>
public sealed record HistoryEntryDto(string Query, int UseCount, DateTimeOffset LastUsedUtc);

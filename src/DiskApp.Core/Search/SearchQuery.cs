namespace DiskApp.Core.Search;

/// <summary>
/// Разобранный поисковый запрос: термы и фразы, фильтры, сортировка, лимит и курсор страницы.
/// Строит парсер (этап 3.1), исполняет поисковый сервис (этапы 3.2–3.5).
/// </summary>
public sealed record SearchQuery
{
    /// <summary>Размер страницы по умолчанию.</summary>
    public const int DefaultLimit = 50;

    /// <summary>Пустой запрос: подборка недавних и избранных.</summary>
    public static SearchQuery Empty { get; } = new();

    /// <summary>Слова и фразы в порядке ввода.</summary>
    public IReadOnlyList<SearchTerm> Terms { get; init; } = [];

    /// <summary>Фильтры <c>ключ:значение</c> в порядке ввода.</summary>
    public IReadOnlyList<SearchFilter> Filters { get; init; } = [];

    /// <summary>Регулярное выражение из <c>/…/</c>; <c>null</c>, если не задано.</summary>
    public string? RegexPattern { get; init; }

    /// <summary>Поле сортировки.</summary>
    public SearchSortField SortField { get; init; } = SearchSortField.Relevance;

    /// <summary>Сортировать по убыванию. Для релевантности всегда по убыванию.</summary>
    public bool SortDescending { get; init; } = true;

    /// <summary>Максимум результатов на странице.</summary>
    public int Limit { get; init; } = DefaultLimit;

    /// <summary>Курсор следующей страницы; <c>null</c> — первая страница.</summary>
    public SearchCursor? Cursor { get; init; }

    /// <summary>Запрос без текста, фильтров и регулярного выражения.</summary>
    public bool IsEmpty => Terms.Count == 0 && Filters.Count == 0 && RegexPattern is null;

    /// <summary>Есть ли положительные термы, по которым можно искать в FTS.</summary>
    public bool HasPositiveTerms
    {
        get
        {
            foreach (var term in Terms)
            {
                if (!term.IsNegated)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Фильтры с указанным ключом в порядке ввода.
    /// </summary>
    public IEnumerable<SearchFilter> FiltersOf(SearchFilterKey key) => Filters.Where(filter => filter.Key == key);
}

/// <summary>
/// Слово или фраза запроса.
/// </summary>
/// <param name="Text">Текст без кавычек и знака отрицания.</param>
/// <param name="IsPhrase">Фраза в кавычках: слова идут подряд.</param>
/// <param name="IsNegated">Отрицание: записи с таким текстом исключаются.</param>
public sealed record SearchTerm(string Text, bool IsPhrase = false, bool IsNegated = false);

/// <summary>
/// Фильтр запроса <c>ключ:значение</c>. Значение хранится как ввёл пользователь; разбор диапазонов и синонимов — на исполнении.
/// </summary>
/// <param name="Key">Ключ фильтра.</param>
/// <param name="Value">Значение: <c>pdf</c>, <c>&gt;10mb</c>, <c>2026-01..2026-03</c>, <c>dir</c>.</param>
/// <param name="IsNegated">Отрицание: записи, подходящие под фильтр, исключаются.</param>
public sealed record SearchFilter(SearchFilterKey Key, string Value, bool IsNegated = false);

/// <summary>
/// Ключи фильтров (README, «Синтаксис поиска»).
/// </summary>
public enum SearchFilterKey
{
    /// <summary><c>ext:</c> — расширение.</summary>
    Extension,

    /// <summary><c>type:</c> — вид файла.</summary>
    Kind,

    /// <summary><c>in:</c> — внутри папки или корня с таким именем.</summary>
    Folder,

    /// <summary><c>root:</c> — конкретный корень.</summary>
    Root,

    /// <summary><c>size:</c> — размер.</summary>
    Size,

    /// <summary><c>modified:</c> — дата изменения.</summary>
    Modified,

    /// <summary><c>created:</c> — дата создания.</summary>
    Created,

    /// <summary><c>is:</c> — признак: <c>dir</c>, <c>file</c>, <c>hidden</c>.</summary>
    Attribute,

    /// <summary><c>tag:</c> — тег.</summary>
    Tag,

    /// <summary><c>fav:</c> — только избранное.</summary>
    Favorite,
}

/// <summary>
/// Поле сортировки результатов.
/// </summary>
public enum SearchSortField
{
    /// <summary>По релевантности (ранжирование).</summary>
    Relevance,

    /// <summary>По имени.</summary>
    Name,

    /// <summary>По дате изменения.</summary>
    Modified,

    /// <summary>По дате создания.</summary>
    Created,

    /// <summary>По размеру.</summary>
    Size,
}

/// <summary>
/// Курсор keyset-пагинации: последняя пара (оценка, идентификатор) предыдущей страницы.
/// </summary>
/// <param name="Score">Оценка последнего результата.</param>
/// <param name="EntryId">Идентификатор последнего результата для детерминированного разрыва ничьих.</param>
public readonly record struct SearchCursor(double Score, long EntryId);

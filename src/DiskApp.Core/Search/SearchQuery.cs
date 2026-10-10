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

    /// <summary>Токены исходной строки с позициями: для подсветки синтаксиса и подсказок.</summary>
    public IReadOnlyList<QueryToken> Tokens { get; init; } = [];

    /// <summary>Замечания разбора: непонятые значения фильтров, неверное регулярное выражение.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

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

    /// <summary>
    /// Каноническая строка запроса: термы, фразы в кавычках, фильтры с каноническими ключами, регулярное выражение.
    /// Разбор этой строки даёт те же термы, фильтры и выражение (round-trip, PLAN.md, этап 3.1).
    /// </summary>
    public override string ToString()
    {
        var parts = new List<string>(Terms.Count + Filters.Count + 1);
        foreach (var term in Terms)
        {
            var text = term.IsPhrase ? $"\"{term.Text}\"" : term.Text;
            parts.Add(term.IsNegated ? "-" + text : text);
        }

        foreach (var filter in Filters)
        {
            var value = filter.Value.Any(char.IsWhiteSpace) || filter.Value.StartsWith('"') ? $"\"{filter.Value}\"" : filter.Value;
            var text = $"{QueryParser.CanonicalKey(filter.Key)}:{value}";
            parts.Add(filter.IsNegated ? "-" + text : text);
        }

        if (RegexPattern is not null)
        {
            parts.Add($"/{RegexPattern}/");
        }

        return string.Join(' ', parts);
    }
}

/// <summary>
/// Токен исходной строки запроса.
/// </summary>
/// <param name="Kind">Вид токена.</param>
/// <param name="Start">Позиция первого символа (включая знак отрицания).</param>
/// <param name="Length">Длина в исходной строке.</param>
/// <param name="Text">Исходный текст токена.</param>
public sealed record QueryToken(QueryTokenKind Kind, int Start, int Length, string Text)
{
    /// <summary>Позиция за последним символом.</summary>
    public int End => Start + Length;
}

/// <summary>
/// Виды токенов запроса.
/// </summary>
public enum QueryTokenKind
{
    /// <summary>Слово.</summary>
    Term,

    /// <summary>Фраза в кавычках.</summary>
    Phrase,

    /// <summary>Фильтр <c>ключ:значение</c>.</summary>
    Filter,

    /// <summary>Фильтр без значения (<c>ext:</c>) — пользователь ещё печатает; при поиске не учитывается.</summary>
    IncompleteFilter,

    /// <summary>Регулярное выражение <c>/…/</c>.</summary>
    Regex,
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

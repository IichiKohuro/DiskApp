using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using DiskApp.Core.Domain;
using DiskApp.Core.Paths;
using DiskApp.Core.Search;

using Microsoft.Data.Sqlite;

namespace DiskApp.Infrastructure.Sqlite.Search;

/// <summary>
/// Выборка кандидатов поиска из SQLite (PLAN.md, этап 3.2; ADR-0005). Источник — по плану: FTS5 trigram по свёрнутому
/// имени, префикс <c>name_folded</c> по B-tree индексу или все записи по фильтрам. Фильтры превращаются в условия только
/// с параметрами — ни одно значение пользователя не попадает в текст SQL. Короткие и отрицательные термы проверяются
/// через <c>instr</c> по <c>name_folded</c> (свёрнутому, поэтому без <c>LIKE</c> и его ASCII-регистра).
/// Долгий запрос прерывается <c>sqlite3_interrupt</c>: по отмене — исключением, по таймауту — частичным результатом.
/// </summary>
public sealed class SqliteSearchIndex(SqliteDatabase database, PathNormalizer? paths = null) : ISearchIndex
{
    /// <summary>Сколько записей добирается сверх лимита: начинающиеся с терма и недавно открытые или избранные.</summary>
    public const int BoostLimit = 500;

    private const int SqliteInterrupt = 9;
    private const long CloudAttributes = 0x00400000 | 0x00040000 | 0x1000;
    private const string Columns = """
        e.id, e.root_id, e.parent_id, e.name, e.ext, e.is_dir, e.size, e.created_utc, e.modified_utc, e.attributes, e.rel_path, e.kind,
        u.open_count, u.last_opened_utc, fv.entry_id IS NOT NULL
        """;

    private const string Joins = """
        LEFT JOIN usage AS u ON u.entry_id = e.id
        LEFT JOIN favorites AS fv ON fv.entry_id = e.id
        """;

    private readonly PathNormalizer _paths = paths ?? PathNormalizer.Current;
    private readonly MemberLoads _memberLoads = new();

    /// <summary>
    /// Сколько времени тратить на подсчёт совпадений при дополнительных условиях; не успели — число «не меньше N».
    /// Выдача не ждёт дорогого точного счёта (FTS по частому слову плюс фильтр).
    /// </summary>
    public TimeSpan CountTimeout { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// До скольких кандидатов источника тега их сортируют целиком при выдаче «новые сверху» (этап 19); больше — обход
    /// индекса времени изменения: при таком числе совпадений он быстро набирает страницу.
    /// </summary>
    private const long SortedSourceLimit = 150_000;

    /// <summary>
    /// До скольких записей тега его проверяют по множеству идентификаторов в памяти (этап 19.5); у тега больше записи
    /// плотные, и обход индекса времени изменения с проверкой пути набирает страницу быстрее, чем строится множество.
    /// </summary>
    private const long MemberSetLimit = 200_000;

    /// <summary>
    /// Во сколько раз шаг обхода индекса времени изменения с проверкой по множеству дешевле, чем чтение строки записи тега
    /// для сортировки (замер <c>search-budget</c>, этап 19.5).
    /// </summary>
    private const double WalkToReadCost = 0.25;

    /// <inheritdoc />
    public Task<CandidateBatch> FindAsync(SearchPlan plan, CandidateQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(query);

        return Task.Run(() =>
        {
            using var connection = database.Connections.Open();
            var conditions = BuildConditions(connection, plan);
            if (conditions is null)
            {
                return CandidateBatch.Empty;
            }

            // Функция множества тега снимается с соединения в конце запроса: в пул соединение уходит без множества.
            using var members = conditions.Members;

            using var timeout = new CancellationTokenSource(query.Timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var collected = new List<SearchCandidate>(Math.Min(query.Limit, 1024));
            var partial = false;
            try
            {
                // Только фильтры: если совпадений больше лимита, сортировка всех по времени дорога — вместо неё
                // обходим индекс времени изменения с конца и останавливаемся на лимите. Число записей одного тега уже
                // известно; тег с условием по индексу (ext:) сортирует немногие совпадения, и пробный подсчёт не нужен.
                var newestFirst = plan.Source == CandidateSource.Filters
                    && (conditions.PlannerChoosesIndex || (conditions.KnownCount ?? CountUpTo(connection, plan, conditions, query.Limit + 1L, linked.Token)) > query.Limit);
                if (newestFirst && conditions.KnownCount is { } known && conditions.Members is not null)
                {
                    conditions.SortSourceByTime = !WalkPays(connection, known, query.Limit);
                }

                var sql = MainSql(plan, conditions, query.Limit, newestFirst);
                LoadMembers(connection, conditions, sql, linked.Token);
                Read(connection, sql, conditions.Parameters, collected, linked.Token);
            }
            catch (Exception exception) when (IsInterruption(exception))
            {
                // Прерывание по отмене пользователя — исключение; по таймауту (в том числе до старта запроса) — частичный результат.
                cancellationToken.ThrowIfCancellationRequested();
                partial = true;
            }

            var truncated = !partial && collected.Count >= query.Limit;
            if (truncated && plan.Source == CandidateSource.FullText)
            {
                AddBoosts(connection, plan, conditions, collected, linked.Token, ref partial);
            }

            return new CandidateBatch(collected, truncated, partial);
        }, cancellationToken);
    }

    private static bool IsInterruption(Exception exception)
        => exception is OperationCanceledException || (exception is SqliteException sqlite && sqlite.SqliteErrorCode == SqliteInterrupt);

    /// <inheritdoc />
    public Task<CandidateCount> CountAsync(SearchPlan plan, long cap, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentOutOfRangeException.ThrowIfLessThan(cap, 1);

        return Task.Run(() =>
        {
            using var connection = database.Connections.Open();
            var conditions = BuildConditions(connection, plan);
            if (conditions is null)
            {
                return new CandidateCount(0, true);
            }

            using var members = conditions.Members;

            try
            {
                // Только тег: число его записей уже посчитано по индексу путей, точно.
                if (conditions.KnownCount is { } known)
                {
                    return new CandidateCount(known, true);
                }

                // Без дополнительных условий счёт берётся прямо из FTS или из индекса имени — дёшево и точно
                // даже для сотен тысяч совпадений; с условиями — не дальше потолка.
                var onlyTerms = conditions.Clauses.Count == 0 && plan.ExcludedTerms.Count == 0;
                if (onlyTerms && plan.Source == CandidateSource.FullText && plan.ShortTerms.Count == 0)
                {
                    conditions.Parameters["$match"] = string.Join(' ', plan.FtsTerms.Select(Quote));
                    return new CandidateCount(Scalar(connection, "SELECT COUNT(*) FROM entries_fts WHERE entries_fts MATCH $match", conditions.Parameters, cancellationToken), true);
                }

                if (onlyTerms && plan.Source == CandidateSource.NamePrefix && plan.ShortTerms.Count == 1)
                {
                    conditions.Parameters["$prefix_from"] = plan.ShortTerms[0];
                    conditions.Parameters["$prefix_to"] = Next(plan.ShortTerms[0]);
                    return new CandidateCount(Scalar(connection, "SELECT COUNT(*) FROM entries WHERE name_folded >= $prefix_from AND name_folded < $prefix_to", conditions.Parameters, cancellationToken), true);
                }

                var (counted, completed) = CountWithin(connection, plan, conditions, cap + 1, CountTimeout, cancellationToken);
                return completed && counted <= cap ? new CandidateCount(counted, true) : new CandidateCount(Math.Min(counted, cap), false);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == SqliteInterrupt)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw;
            }
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SearchCandidate>> FindRecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        return Task.Run<IReadOnlyList<SearchCandidate>>(() =>
        {
            using var connection = database.Connections.Open();
            var collected = new List<SearchCandidate>();
            var parameters = new Dictionary<string, object?> { ["$limit"] = limit };
            Read(
                connection,
                $"""
                SELECT {Columns} FROM entries AS e {Joins}
                WHERE e.id IN (SELECT entry_id FROM usage UNION SELECT entry_id FROM favorites)
                ORDER BY fv.entry_id IS NOT NULL DESC, u.last_opened_utc DESC
                LIMIT $limit
                """,
                parameters,
                collected,
                cancellationToken);

            if (collected.Count < limit)
            {
                var known = collected.Select(static candidate => candidate.Entry.Id).ToHashSet();
                var recent = new List<SearchCandidate>();
                parameters["$limit"] = limit;
                Read(connection, $"SELECT {Columns} FROM entries AS e {Joins} WHERE e.is_dir = 0 ORDER BY e.modified_utc DESC LIMIT $limit", parameters, recent, cancellationToken);
                collected.AddRange(recent.Where(candidate => !known.Contains(candidate.Entry.Id)).Take(limit - collected.Count));
            }

            return collected;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SearchCandidate>> FindByAnyTrigramAsync(SearchPlan plan, IReadOnlyList<string> trigrams, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(trigrams);
        if (trigrams.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<SearchCandidate>>([]);
        }

        return Task.Run<IReadOnlyList<SearchCandidate>>(() =>
        {
            using var connection = database.Connections.Open();
            // Кандидаты идут из FTS: источник от фильтров не нужен, хотя без термов план выглядит как «только фильтры».
            var looser = plan with { FtsTerms = [], ShortTerms = [] };
            var conditions = BuildConditions(connection, looser, offerSource: false);
            if (conditions is null)
            {
                return [];
            }

            conditions.Parameters["$match"] = string.Join(" OR ", trigrams.Select(Quote));
            conditions.Parameters["$limit"] = limit;
            var collected = new List<SearchCandidate>();

            // Короткие термы — буквальные требования, нечёткость их не касается.
            var where = Where(looser, conditions);
            var shortClauses = new List<string>();
            AddInstr(shortClauses, conditions, plan.ShortTerms, "$fuzzy_short", contains: true);
            if (shortClauses.Count > 0)
            {
                where += " AND " + string.Join(" AND ", shortClauses);
            }

            Read(
                connection,
                $"SELECT {Columns} FROM entries_fts JOIN entries AS e ON e.id = entries_fts.rowid {Joins} WHERE entries_fts MATCH $match AND {where} LIMIT $limit",
                conditions.Parameters,
                collected,
                cancellationToken);
            return collected;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetTopExtensionsAsync(string prefix, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prefix);

        return Task.Run<IReadOnlyList<string>>(() =>
        {
            using var connection = database.Connections.Open();
            var folded = SearchText.Fold(prefix.TrimStart('.'));
            var sql = folded.Length == 0
                ? "SELECT ext FROM entries WHERE is_dir = 0 AND ext <> '' GROUP BY ext ORDER BY COUNT(*) DESC LIMIT $limit"
                : "SELECT ext FROM entries WHERE is_dir = 0 AND ext >= $from AND ext < $to GROUP BY ext ORDER BY COUNT(*) DESC LIMIT $limit";
            using var command = Command(connection, sql, new Dictionary<string, object?> { ["$limit"] = limit, ["$from"] = folded, ["$to"] = Next(folded) });
            using var registration = cancellationToken.Register(() => Interrupt(connection));
            using var reader = command.ExecuteReader();
            var result = new List<string>();
            while (reader.Read())
            {
                result.Add(reader.GetString(0));
            }

            return result;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> FindFolderNamesAsync(string prefix, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);

        return Task.Run<IReadOnlyList<string>>(() =>
        {
            using var connection = database.Connections.Open();
            var folded = SearchText.Fold(prefix);
            using var command = Command(
                connection,
                "SELECT DISTINCT name FROM entries WHERE name_folded >= $from AND name_folded < $to AND is_dir = 1 LIMIT $limit",
                new Dictionary<string, object?> { ["$from"] = folded, ["$to"] = Next(folded), ["$limit"] = limit });
            using var reader = command.ExecuteReader();
            var result = new List<string>();
            while (reader.Read())
            {
                result.Add(reader.GetString(0));
            }

            return result;
        }, cancellationToken);
    }

    /// <summary>
    /// Строка запроса FTS5 для терма: в двойных кавычках, внутренние кавычки удвоены — спецсимволы FTS (<c>*</c>, <c>-</c>,
    /// <c>:</c>, <c>^</c>, <c>NOT</c>) внутри кавычек теряют особый смысл.
    /// </summary>
    internal static string Quote(string term) => "\"" + term.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>Наименьшая строка больше всех строк с данным префиксом (для диапазона по индексу).</summary>
    internal static string Next(string prefix)
    {
        if (prefix.Length == 0)
        {
            return "￿";
        }

        var last = prefix[^1];
        return last == char.MaxValue ? prefix + "￿" : string.Concat(prefix.AsSpan(0, prefix.Length - 1), ((char)(last + 1)).ToString());
    }

    /// <summary>
    /// SQL основного запроса кандидатов; виден тестам для проверки параметризации.
    /// </summary>
    internal static string MainSql(SearchPlan plan, Conditions conditions, int limit, bool newestFirst = false)
    {
        conditions.Parameters["$limit"] = limit;
        var order = plan.Source switch
        {
            CandidateSource.NamePrefix => "ORDER BY e.name_folded",
            CandidateSource.Filters when newestFirst => "ORDER BY e.modified_utc DESC",
            _ => string.Empty,
        };

        var walksTimeIndex = newestFirst && !conditions.PlannerChoosesIndex && !(conditions.SortSourceByTime && conditions.ScopeJoinSource is not null);
        var (source, fromScope) = plan.Source != CandidateSource.Filters || conditions.PlannerChoosesIndex ? (Source(plan), false)
            : walksTimeIndex ? ("entries AS e INDEXED BY ix_entries_modified", false)
            : CountSource(plan, conditions);
        var where = Where(plan, conditions, fromScope);
        if (plan.Source == CandidateSource.Filters && newestFirst && !walksTimeIndex)
        {
            // Совпадения сортируются: сортировщик держит только время и идентификатор, строки и сведения об открытиях
            // читаются потом для одной страницы, а не для всех совпадений.
            return $"SELECT {Columns} FROM (SELECT e.id FROM {source} WHERE {where} {order} LIMIT $limit) AS top CROSS JOIN entries AS e ON e.id = top.id {Joins}";
        }

        return $"SELECT {Columns} FROM {source} {Joins} WHERE {where} {order} LIMIT $limit";
    }

    private static string Source(SearchPlan plan)
        => plan.Source == CandidateSource.FullText ? "entries_fts JOIN entries AS e ON e.id = entries_fts.rowid" : "entries AS e";

    /// <summary>Откуда считать совпадения и читать их без порядка: источник фильтров, если он есть и уместен.</summary>
    private static (string Source, bool FromScope) CountSource(SearchPlan plan, Conditions conditions)
        => plan.Source == CandidateSource.Filters && !conditions.PlannerChoosesIndex && conditions.ScopeJoinSource is { } join ? (join, true) : (Source(plan), false);

    /// <summary>
    /// Сколько записей подходит под план, но не больше <paramref name="upTo"/>: подзапрос с LIMIT останавливает обход.
    /// </summary>
    private static long CountUpTo(SqliteConnection connection, SearchPlan plan, Conditions conditions, long upTo, CancellationToken cancellationToken)
    {
        conditions.Parameters["$count_limit"] = upTo;
        var (source, fromScope) = CountSource(plan, conditions);
        var sql = $"SELECT COUNT(*) FROM (SELECT 1 FROM {source} WHERE {Where(plan, conditions, fromScope)} LIMIT $count_limit)";
        LoadMembers(connection, conditions, sql, cancellationToken);
        return Scalar(connection, sql, conditions.Parameters, cancellationToken);
    }

    /// <summary>
    /// Выгоднее ли для «новых сверху» обойти индекс времени изменения с проверкой тега по множеству, чем прочитать и
    /// отсортировать все <paramref name="members"/> записей тега: обход проходит в среднем limit · всего / members записей
    /// индекса, но каждая — проверка в памяти без чтения строки; сортировка читает строку каждой записи тега.
    /// </summary>
    private static bool WalkPays(SqliteConnection connection, long members, int limit)
    {
        var total = Scalar(connection, "SELECT COALESCE(MAX(id), 0) FROM entries", new Dictionary<string, object?>(), CancellationToken.None);
        return (double)members * members > limit * (double)total * WalkToReadCost;
    }

    /// <summary>
    /// Строит множество записей тега перед запросом, который проверяет тег по нему; запросы от источника тега его не
    /// проверяют и множества не ждут.
    /// </summary>
    private static void LoadMembers(SqliteConnection connection, Conditions conditions, string sql, CancellationToken cancellationToken)
    {
        if (conditions.Members is { } members && sql.Contains(members.Function, StringComparison.Ordinal))
        {
            members.Load(connection, conditions.Parameters, cancellationToken);
        }
    }

    /// <summary>
    /// Считает совпадения построчно, не дольше <paramref name="budget"/> и не больше <paramref name="upTo"/>:
    /// при прерывании возвращает, сколько успели, и признак незавершённости. Отмена пользователем — исключение.
    /// </summary>
    private static (long Count, bool Completed) CountWithin(
        SqliteConnection connection,
        SearchPlan plan,
        Conditions conditions,
        long upTo,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        conditions.Parameters["$count_limit"] = upTo;
        var (source, fromScope) = CountSource(plan, conditions);
        var sql = $"SELECT 1 FROM {source} WHERE {Where(plan, conditions, fromScope)} LIMIT $count_limit";

        // Множество записей тега — подготовка, а не подсчёт: в бюджет не входит, его прерывает только отмена.
        LoadMembers(connection, conditions, sql, cancellationToken);
        using var command = Command(connection, sql, conditions.Parameters);
        using var timeout = new CancellationTokenSource(budget);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var registration = linked.Token.Register(() => Interrupt(connection));
        long counted = 0;
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                counted++;
            }

            return (counted, true);
        }
        catch (Exception exception) when (IsInterruption(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return (counted, false);
        }
    }

    private static long Scalar(SqliteConnection connection, string sql, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var command = Command(connection, sql, parameters);
        using var registration = cancellationToken.Register(() => Interrupt(connection));
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <param name="plan">План запроса.</param>
    /// <param name="conditions">Условия фильтров.</param>
    /// <param name="fromScope">Запрос читает источник фильтров: его условие источник уже выполнил, повторная проверка лишняя.</param>
    private static string Where(SearchPlan plan, Conditions conditions, bool fromScope = false)
    {
        var clauses = new List<string>(conditions.Clauses);
        if (fromScope && conditions.SourceClauseIndex is { } covered)
        {
            clauses.RemoveAt(covered);
        }

        switch (plan.Source)
        {
            case CandidateSource.FullText:
                conditions.Parameters["$match"] = string.Join(' ', plan.FtsTerms.Select(Quote));
                clauses.Insert(0, "entries_fts MATCH $match");
                AddInstr(clauses, conditions, plan.ShortTerms, "$short", contains: true);
                break;
            case CandidateSource.NamePrefix:
                conditions.Parameters["$prefix_from"] = plan.ShortTerms[0];
                conditions.Parameters["$prefix_to"] = Next(plan.ShortTerms[0]);
                clauses.Insert(0, "e.name_folded >= $prefix_from AND e.name_folded < $prefix_to");
                AddInstr(clauses, conditions, plan.ShortTerms.Skip(1).ToArray(), "$short", contains: true);
                break;
            default:
                break;
        }

        AddInstr(clauses, conditions, plan.ExcludedTerms, "$excluded", contains: false);
        return clauses.Count == 0 ? "1" : string.Join(" AND ", clauses);
    }

    private static void AddInstr(List<string> clauses, Conditions conditions, IReadOnlyList<string> terms, string prefix, bool contains)
    {
        for (var i = 0; i < terms.Count; i++)
        {
            var name = $"{prefix}{i}";
            conditions.Parameters[name] = terms[i];
            clauses.Add(contains ? $"instr(e.name_folded, {name}) > 0" : $"instr(e.name_folded, {name}) = 0");
        }
    }

    private static void Interrupt(SqliteConnection connection)
    {
        if (connection.Handle is { } handle)
        {
            SQLitePCL.raw.sqlite3_interrupt(handle);
        }
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, IReadOnlyDictionary<string, object?> parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            if (sql.Contains(name, StringComparison.Ordinal))
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }
        }

        return command;
    }

    private static void Read(SqliteConnection connection, string sql, IReadOnlyDictionary<string, object?> parameters, List<SearchCandidate> target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var command = Command(connection, sql, parameters);
        using var registration = cancellationToken.Register(() => Interrupt(connection));

        // Отмена до старта запроса не прерывает его (sqlite3_interrupt без выполняющихся запросов ничего не делает).
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            target.Add(new SearchCandidate(
                new FileEntry
                {
                    Id = reader.GetInt64(0),
                    RootId = reader.GetInt64(1),
                    ParentId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    Name = reader.GetString(3),
                    Extension = reader.GetString(4),
                    IsDirectory = reader.GetInt64(5) != 0,
                    Size = reader.GetInt64(6),
                    CreatedUtc = SqliteTime.Decode(reader.GetInt64(7)),
                    ModifiedUtc = SqliteTime.Decode(reader.GetInt64(8)),
                    Attributes = (FileAttributes)reader.GetInt64(9),
                    RelPath = reader.GetString(10),
                    Kind = (FileKind)reader.GetInt64(11),
                },
                reader.IsDBNull(12) ? 0 : reader.GetInt32(12),
                reader.IsDBNull(13) ? null : SqliteTime.Decode(reader.GetInt64(13)),
                reader.GetInt64(14) != 0));
        }
    }

    private static void AddBoosts(SqliteConnection connection, SearchPlan plan, Conditions conditions, List<SearchCandidate> collected, CancellationToken cancellationToken, ref bool partial)
    {
        // Совпадений больше лимита: основная выборка идёт в порядке rowid и может не содержить лучших записей.
        // Добираем те, что ранжирование поднимет выше всего: имя начинается с терма, недавно открытые, избранные.
        var known = collected.Select(static candidate => candidate.Entry.Id).ToHashSet();
        var nameConditions = plan.FtsTerms.Select((term, index) => (Name: $"$boost{index}", Term: term)).ToArray();
        foreach (var (name, term) in nameConditions)
        {
            conditions.Parameters[name] = term;
        }

        var termClauses = string.Join(" AND ", nameConditions.Select(static item => $"instr(e.name_folded, {item.Name}) > 0"));
        var looser = plan with { FtsTerms = [], ShortTerms = [] };
        var where = Where(looser, conditions);
        conditions.Parameters["$boost_limit"] = BoostLimit;
        conditions.Parameters["$boost_from"] = plan.FtsTerms[0];
        conditions.Parameters["$boost_to"] = Next(plan.FtsTerms[0]);

        string[] queries =
        [
            $"SELECT {Columns} FROM entries AS e {Joins} WHERE e.name_folded >= $boost_from AND e.name_folded < $boost_to AND {termClauses} AND {where} LIMIT $boost_limit",
            $"SELECT {Columns} FROM entries AS e {Joins} WHERE e.id IN (SELECT entry_id FROM usage UNION SELECT entry_id FROM favorites) AND {termClauses} AND {where} LIMIT $boost_limit",
        ];

        foreach (var sql in queries)
        {
            var extra = new List<SearchCandidate>();
            try
            {
                Read(connection, sql, conditions.Parameters, extra, cancellationToken);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == SqliteInterrupt)
            {
                partial = true;
                return;
            }

            foreach (var candidate in extra)
            {
                if (known.Add(candidate.Entry.Id))
                {
                    collected.Add(candidate);
                }
            }
        }
    }

    /// <summary>
    /// Условия фильтров плана; <c>null</c>, если план заведомо ничего не находит (например, папки <c>in:</c> не нашлись).
    /// </summary>
    /// <param name="connection">Соединение запроса: на нём регистрируются функции и временные таблицы.</param>
    /// <param name="plan">План.</param>
    /// <param name="offerSource">Можно ли тегу стать источником кандидатов, если план только из фильтров.</param>
    private Conditions? BuildConditions(SqliteConnection connection, SearchPlan plan, bool offerSource = true)
    {
        var conditions = new Conditions();
        if (plan.RegexPattern is { } pattern)
        {
            var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
            connection.CreateFunction("diskapp_regex", (string name) => regex.IsMatch(name), isDeterministic: true);
            conditions.Clauses.Add("diskapp_regex(e.name)");
        }

        AddList(conditions, "e.ext", plan.IncludeExtensions, "$ie", negate: false);
        AddList(conditions, "e.ext", plan.ExcludeExtensions, "$ee", negate: true);
        AddList(conditions, "e.kind", plan.IncludeKinds.Select(static kind => (object)(long)kind).ToArray(), "$ik", negate: false);
        AddList(conditions, "e.kind", plan.ExcludeKinds.Select(static kind => (object)(long)kind).ToArray(), "$ek", negate: true);
        AddList(conditions, "e.root_id", plan.IncludeRoots.Cast<object>().ToArray(), "$ir", negate: false);
        AddList(conditions, "e.root_id", plan.ExcludeRoots.Cast<object>().ToArray(), "$er", negate: true);

        if (plan.IncludeFolders.Count > 0)
        {
            var clause = FolderClause(connection, plan.IncludeFolders, conditions, "include");
            if (clause is null)
            {
                return null;
            }

            if (conditions.ScopeJoinSource is not null)
            {
                conditions.SourceClauseIndex = conditions.Clauses.Count;
            }

            conditions.Clauses.Add(clause);
        }

        if (plan.ExcludeFolders.Count > 0 && FolderClause(connection, plan.ExcludeFolders, conditions, "exclude") is { } excluded)
        {
            conditions.Clauses.Add($"NOT ({excluded})");
        }

        // У папок размер 0, поэтому для диапазонов от 1 байта проверка «это файл» лишняя и только мешает счёту по индексу размера.
        AddRanges(conditions, Sizes(plan.IncludeSizes), "e.size", "$is", inclusiveMax: true, negate: false, filesOnlyWhenFromZero: true);
        AddRanges(conditions, Sizes(plan.ExcludeSizes), "e.size", "$es", inclusiveMax: true, negate: true, filesOnlyWhenFromZero: true);
        AddRanges(conditions, Dates(plan.IncludeModified), "e.modified_utc", "$im", inclusiveMax: false, negate: false, filesOnlyWhenFromZero: false);
        AddRanges(conditions, Dates(plan.ExcludeModified), "e.modified_utc", "$em", inclusiveMax: false, negate: true, filesOnlyWhenFromZero: false);
        AddRanges(conditions, Dates(plan.IncludeCreated), "e.created_utc", "$ic", inclusiveMax: false, negate: false, filesOnlyWhenFromZero: false);
        AddRanges(conditions, Dates(plan.ExcludeCreated), "e.created_utc", "$ec", inclusiveMax: false, negate: true, filesOnlyWhenFromZero: false);

        foreach (var attribute in plan.IncludeAttributes)
        {
            conditions.Clauses.Add(AttributeClause(attribute));
        }

        foreach (var attribute in plan.ExcludeAttributes)
        {
            conditions.Clauses.Add($"NOT ({AttributeClause(attribute)})");
        }

        // Тег у записи — свой или авто-тег папки выше (этап 19): «tag:x» — любой из двух, «-tag:x» — ни одного.
        offerSource &= plan.Source == CandidateSource.Filters;
        for (var i = 0; i < plan.IncludeTags.Count; i++)
        {
            conditions.Parameters[$"$it{i}"] = plan.IncludeTags[i];
            var offered = offerSource && i == 0 && conditions.ScopeJoinSource is null;
            var clause = TagClause(connection, plan.IncludeTags[i], $"$it{i}", $"diskapp_autotag_it{i}", offered ? conditions : null);
            if (offered && conditions.ScopeJoinSource is not null)
            {
                conditions.SourceClauseIndex = conditions.Clauses.Count;
            }

            conditions.Clauses.Add(clause);
        }

        for (var i = 0; i < plan.ExcludeTags.Count; i++)
        {
            conditions.Parameters[$"$et{i}"] = plan.ExcludeTags[i];
            conditions.Clauses.Add($"NOT {TagClause(connection, plan.ExcludeTags[i], $"$et{i}", $"diskapp_autotag_et{i}")}");
        }

        if (plan.Favorite is { } favorite)
        {
            conditions.Clauses.Add(favorite ? "EXISTS (SELECT 1 FROM favorites AS f2 WHERE f2.entry_id = e.id)" : "NOT EXISTS (SELECT 1 FROM favorites AS f2 WHERE f2.entry_id = e.id)");
        }

        if (conditions.SourceCount is { } members)
        {
            if (conditions.Clauses.Count == 1 && plan.ExcludedTerms.Count == 0)
            {
                // Только тег: число известно точно. Читать ли «новые сверху» обходом индекса времени или сортировкой
                // записей тега, решает выборка — от лимита страницы зависит, что дешевле.
                conditions.KnownCount = members;
            }
            else if (conditions.Members is not null && HasIndexedFilter(plan))
            {
                // Тег и условие с индексом (ext:, type:, размер, дата изменения): тег проверяется по множеству без чтения
                // строки, и планировщик SQLite обходит индекс этого условия — сотни записей вместо всех записей тега.
                conditions.PlannerChoosesIndex = true;
            }
            else
            {
                // Остальные условия без индекса: совпадения редки, и дешевле отсортировать немногих кандидатов тега, чем
                // обходить индекс времени изменения.
                conditions.SortSourceByTime = members <= SortedSourceLimit;
            }
        }

        return conditions;
    }

    private static bool HasIndexedFilter(SearchPlan plan)
        => plan.IncludeExtensions.Count + plan.IncludeKinds.Count + plan.IncludeSizes.Count + plan.IncludeModified.Count > 0;

    private static (object? From, object? To)[] Dates(IReadOnlyList<DateRange> ranges)
        => ranges.Select(static range => ((object?)SqliteTime.Encode(range.FromUtc), (object?)SqliteTime.Encode(range.ToUtc))).ToArray();

    private static (object? From, object? To)[] Sizes(IReadOnlyList<SizeRange> ranges)
        => ranges.Select(static range => ((object?)range.Min, (object?)(range.Max == long.MaxValue ? null : range.Max))).ToArray();

    private static string AttributeClause(AttributeFilter attribute) => attribute switch
    {
        AttributeFilter.Directory => "e.is_dir = 1",
        AttributeFilter.File => "e.is_dir = 0",
        AttributeFilter.Hidden => "(e.attributes & 2) <> 0",
        AttributeFilter.Favorite => "EXISTS (SELECT 1 FROM favorites AS f3 WHERE f3.entry_id = e.id)",
        _ => $"(e.attributes & {CloudAttributes.ToString(CultureInfo.InvariantCulture)}) <> 0",
    };

    private static void AddList<T>(Conditions conditions, string column, IReadOnlyList<T> values, string prefix, bool negate)
    {
        if (values.Count == 0)
        {
            return;
        }

        var names = new List<string>(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            var name = $"{prefix}{i}";
            conditions.Parameters[name] = values[i];
            names.Add(name);
        }

        conditions.Clauses.Add($"{column} {(negate ? "NOT IN" : "IN")} ({string.Join(", ", names)})");
    }

    private static void AddRanges(Conditions conditions, (object? From, object? To)[] ranges, string column, string prefix, bool inclusiveMax, bool negate, bool filesOnlyWhenFromZero)
    {
        if (ranges.Length == 0)
        {
            return;
        }

        var alternatives = new List<string>();
        for (var i = 0; i < ranges.Length; i++)
        {
            var parts = new List<string>();
            if (filesOnlyWhenFromZero && ranges[i].From is null or 0L)
            {
                parts.Add("e.is_dir = 0");
            }

            if (ranges[i].From is { } from)
            {
                conditions.Parameters[$"{prefix}{i}a"] = from;
                parts.Add($"{column} >= {prefix}{i}a");
            }

            if (ranges[i].To is { } to)
            {
                conditions.Parameters[$"{prefix}{i}b"] = to;
                parts.Add($"{column} {(inclusiveMax ? "<=" : "<")} {prefix}{i}b");
            }

            alternatives.Add(parts.Count == 0 ? "1" : "(" + string.Join(" AND ", parts) + ")");
        }

        var clause = "(" + string.Join(" OR ", alternatives) + ")";
        conditions.Clauses.Add(negate ? $"NOT {clause}" : clause);
    }

    /// <summary>
    /// Условие «внутри одной из папок». Папки по имени разрешаются все (их может быть сотни — «2025», «src»), диапазоны
    /// потомков сливаются в непересекающиеся (вложенный диапазон поглощается внешним) и кладутся во временную таблицу
    /// соединения с индексом. Для записи проверяется один ближайший слева диапазон — поиск по индексу, а не перебор.
    /// </summary>
    private string? FolderClause(SqliteConnection connection, IReadOnlyList<FolderScope> scopes, Conditions conditions, string kind)
    {
        var separator = _paths.Separator;
        var ranges = new List<(long RootId, string From, string To)>();
        var wholeRoots = new List<long>();
        List<(long Id, string Path)>? roots = null;
        foreach (var scope in scopes)
        {
            if (scope.FolderName is { } folderName)
            {
                var folders = new List<(long RootId, string RelPath)>();
                using (var command = Command(
                    connection,
                    "SELECT root_id, rel_path FROM entries WHERE name_folded = $name AND is_dir = 1",
                    new Dictionary<string, object?> { ["$name"] = folderName }))
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        folders.Add((reader.GetInt64(0), reader.GetString(1)));
                    }
                }

                foreach (var (rootId, relPath) in folders)
                {
                    ranges.Add((rootId, relPath + separator, relPath + (char)(separator + 1)));

                    // Корни внутри найденной папки — часть её области: их файлы физически лежат в ней (ADR-0015).
                    roots ??= LoadRoots(connection);
                    if (roots.FirstOrDefault(root => root.Id == rootId).Path is { } rootPath)
                    {
                        var folder = _paths.Combine(rootPath, relPath);
                        foreach (var nested in roots)
                        {
                            if (_paths.IsNestedIn(nested.Path, folder) && !wholeRoots.Contains(nested.Id))
                            {
                                wholeRoots.Add(nested.Id);
                            }
                        }
                    }
                }
            }
            else if (scope.RootId is { } rootId)
            {
                if (string.IsNullOrEmpty(scope.RelPath))
                {
                    wholeRoots.Add(rootId);
                }
                else
                {
                    ranges.Add((rootId, scope.RelPath + separator, scope.RelPath + (char)(separator + 1)));
                }
            }
        }

        if (ranges.Count == 0 && wholeRoots.Count == 0)
        {
            return null;
        }

        var alternatives = new List<string>();
        if (wholeRoots.Count > 0)
        {
            var names = new List<string>();
            for (var i = 0; i < wholeRoots.Count; i++)
            {
                conditions.Parameters[$"$scope_{kind}_root{i}"] = wholeRoots[i];
                names.Add($"$scope_{kind}_root{i}");
            }

            alternatives.Add($"e.root_id IN ({string.Join(", ", names)})");
        }

        if (ranges.Count > 0)
        {
            var table = $"diskapp_scope_{kind}";
            var disjoint = Disjoint(ranges);
            FillScopeTable(connection, table, disjoint);

            // Проверка строки — бинарный поиск в памяти через функцию .NET: дешевле коррелированного подзапроса.
            var set = new ScopeSet(disjoint);
            var function = $"diskapp_in_{kind}";
            connection.CreateFunction(function, (long rootId, string relPath) => set.Contains(rootId, relPath), isDeterministic: true);
            alternatives.Add($"{function}(e.root_id, e.rel_path)");

            // Если других корней в условии нет, подсчёт и выборка только по фильтрам идут от диапазонов по индексу путей.
            if (kind == "include" && wholeRoots.Count == 0)
            {
                conditions.ScopeJoinSource = $"temp.{table} AS s CROSS JOIN entries AS e ON e.root_id = s.root_id AND e.rel_path >= s.from_ AND e.rel_path < s.to_";
            }
        }

        return "(" + string.Join(" OR ", alternatives) + ")";
    }

    /// <summary>
    /// Условие «у записи есть тег»: свой тег (<c>entry_tags</c>) или авто-тег папки выше (<c>folder_tags</c>, этап 19). Папки с
    /// авто-тегом переводятся в корень и относительный путь; проверка строки — сама папка или диапазон её потомков бинарным
    /// поиском в памяти, как у фильтра <c>in:</c>. Корни внутри папки с авто-тегом входят в её область целиком: их файлы
    /// физически лежат в ней (ADR-0015). Папка вне добавленных корней не действует, пока корень не вернётся.
    /// </summary>
    /// <param name="connection">Соединение запроса: на нём регистрируется функция проверки.</param>
    /// <param name="foldedTag">Имя тега в нижнем регистре.</param>
    /// <param name="parameter">Параметр с именем тега.</param>
    /// <param name="function">Имя функции проверки строки.</param>
    /// <param name="sourceFor">
    /// Условия, которым можно задать источник кандидатов для запроса только из фильтров: записи с тегом и диапазоны папок по
    /// индексу путей вместо обхода всех записей. <c>null</c> — только условие.
    /// </param>
    private string TagClause(SqliteConnection connection, string foldedTag, string parameter, string function, Conditions? sourceFor = null)
    {
        var own = $"EXISTS (SELECT 1 FROM entry_tags AS et JOIN tags AS t ON t.id = et.tag_id WHERE et.entry_id = e.id AND t.name_folded = {parameter})";
        var ownSource = $"SELECT et.entry_id AS id FROM entry_tags AS et JOIN tags AS t ON t.id = et.tag_id WHERE t.name_folded = {parameter}";
        var folders = new List<string>();
        using (var command = Command(
            connection,
            "SELECT ft.path FROM folder_tags AS ft JOIN tags AS t ON t.id = ft.tag_id WHERE t.name_folded = $tag",
            new Dictionary<string, object?> { ["$tag"] = foldedTag }))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                folders.Add(reader.GetString(0));
            }
        }

        if (folders.Count == 0)
        {
            return OfferMembers(connection, sourceFor, function, own, ownSource, ownSource, [ownSource], foldedTag);
        }

        var roots = LoadRoots(connection);
        var separator = _paths.Separator;
        var exact = new HashSet<(long RootId, string RelPath)>();
        var wholeRoots = new HashSet<long>();
        var ranges = new List<(long RootId, string From, string To)>();
        foreach (var folder in folders)
        {
            var root = roots
                .Where(candidate => _paths.Comparer.Equals(candidate.Path, folder) || _paths.IsNestedIn(folder, candidate.Path))
                .OrderByDescending(static candidate => candidate.Path.Length)
                .FirstOrDefault();
            if (root.Path is null || !_paths.TryGetRelativePath(root.Path, folder, out var relative))
            {
                continue;
            }

            foreach (var nested in roots)
            {
                if (_paths.IsNestedIn(nested.Path, folder))
                {
                    wholeRoots.Add(nested.Id);
                }
            }

            if (relative.Length == 0)
            {
                wholeRoots.Add(root.Id);
                continue;
            }

            // Сама папка — точным совпадением: диапазон «417»…«417]» захватил бы и соседнюю «417 ТЗ».
            exact.Add((root.Id, relative));
            ranges.Add((root.Id, relative + separator, relative + (char)(separator + 1)));
        }

        if (exact.Count == 0 && wholeRoots.Count == 0)
        {
            return OfferMembers(connection, sourceFor, function, own, ownSource, ownSource, [ownSource], foldedTag);
        }

        // Папки внутри целых корней ими уже покрыты: части источника не должны пересекаться.
        ranges.RemoveAll(range => wholeRoots.Contains(range.RootId));
        exact.RemoveWhere(folder => wholeRoots.Contains(folder.RootId));
        var disjoint = Disjoint(ranges);
        var set = new ScopeSet(disjoint);
        connection.CreateFunction(
            function,
            (long rootId, string relPath) => wholeRoots.Contains(rootId) || exact.Contains((rootId, relPath)) || set.Contains(rootId, relPath),
            isDeterministic: true);
        var clause = $"({own} OR {function}(e.root_id, e.rel_path))";
        if (sourceFor is not { ScopeJoinSource: null })
        {
            return clause;
        }

        // Запрос только из фильтров берёт кандидатов не обходом всех записей, а объединением: свои теги вне папок, целые
        // корни, сами папки и диапазоны их потомков по индексу путей. Части не пересекаются (свои теги внутри папок, папки
        // внутри диапазонов и всё внутри целых корней отброшены), поэтому UNION ALL — поток без устранения повторов, LIMIT
        // останавливает его рано, а сумма счётчиков частей — точное число записей тега.
        var rangeTable = $"diskapp_tagr_{function}";
        var exactTable = $"diskapp_tagx_{function}";
        FillScopeTable(connection, rangeTable, disjoint);
        FillScopeTable(connection, exactTable, [.. exact.Where(folder => !set.Contains(folder.RootId, folder.RelPath)).Select(static folder => (folder.RootId, folder.RelPath, folder.RelPath))]);
        List<string> parts =
        [
            $"SELECT et.entry_id AS id FROM entry_tags AS et JOIN tags AS t ON t.id = et.tag_id JOIN entries AS o ON o.id = et.entry_id WHERE t.name_folded = {parameter} AND NOT {function}(o.root_id, o.rel_path)",
            $"SELECT r.id FROM temp.{rangeTable} AS s CROSS JOIN entries AS r ON r.root_id = s.root_id AND r.rel_path >= s.from_ AND r.rel_path < s.to_",
            $"SELECT x.id FROM temp.{exactTable} AS s CROSS JOIN entries AS x ON x.root_id = s.root_id AND x.rel_path = s.from_",
        ];

        if (wholeRoots.Count > 0)
        {
            var names = wholeRoots.Select((rootId, index) =>
            {
                var name = $"${function}_root{index}";
                sourceFor.Parameters[name] = rootId;
                return name;
            });
            parts.Add($"SELECT w.id FROM entries AS w WHERE w.root_id IN ({string.Join(", ", names)})");
        }

        // Для множества записей повторы не мешают: свои теги берутся все, без проверки строки.
        var members = string.Join(" UNION ALL ", [ownSource, .. parts.Skip(1)]);
        // Состав тега определяют его папки и набор корней: по ним параллельные запросы делят одно множество.
        var key = string.Join('\u001f', [foldedTag, .. folders, .. roots.Select(static root => $"{root.Id}:{root.Path}")]);
        return OfferMembers(connection, sourceFor, function, clause, string.Join(" UNION ALL ", parts), members, parts, key);
    }

    /// <summary>
    /// Делает записи тега источником кандидатов запроса только из фильтров, если его не задал фильтр <c>in:</c>, и считает их
    /// точно — по индексам, без чтения строк. Если записей тега не больше <see cref="MemberSetLimit"/>, условие тега
    /// становится проверкой идентификатора по множеству в памяти: её можно вычислить по любому индексу записей без чтения
    /// строки, поэтому обход индекса времени изменения или индекса условия <c>ext:</c> почти ничего не стоит.
    /// </summary>
    /// <returns>Условие тега для запроса.</returns>
    private string OfferMembers(SqliteConnection connection, Conditions? conditions, string function, string clause, string source, string membersSql, IReadOnlyList<string> disjointParts, string key)
    {
        if (conditions is not { ScopeJoinSource: null })
        {
            return clause;
        }

        var count = Scalar(connection, "SELECT " + string.Join(" + ", disjointParts.Select(static part => $"(SELECT COUNT(*) FROM ({part}))")), conditions.Parameters, CancellationToken.None);
        conditions.ScopeJoinSource = $"({source}) AS c CROSS JOIN entries AS e ON e.id = c.id";
        conditions.SourceCount = count;
        if (count > MemberSetLimit)
        {
            return clause;
        }

        var members = new TagMembers($"{function}_ids", membersSql, count, key, _memberLoads);
        members.Register(connection);
        conditions.Members = members;
        return $"{members.Function}(e.id)";
    }

    private static List<(long Id, string Path)> LoadRoots(SqliteConnection connection)
    {
        var roots = new List<(long Id, string Path)>();
        using var command = Command(connection, "SELECT id, path FROM roots", new Dictionary<string, object?>());
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            roots.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        return roots;
    }

    private static List<(long RootId, string From, string To)> Disjoint(List<(long RootId, string From, string To)> ranges)
    {
        // Диапазоны потомков папок либо не пересекаются, либо вложены: после сортировки вложенный идёт сразу за внешним.
        ranges.Sort(static (left, right) =>
        {
            var byRoot = left.RootId.CompareTo(right.RootId);
            return byRoot != 0 ? byRoot : string.CompareOrdinal(left.From, right.From);
        });

        var result = new List<(long RootId, string From, string To)>(ranges.Count);
        foreach (var range in ranges)
        {
            if (result.Count > 0 && result[^1].RootId == range.RootId && string.CompareOrdinal(range.From, result[^1].To) < 0)
            {
                continue;
            }

            result.Add(range);
        }

        return result;
    }

    /// <summary>
    /// Непересекающиеся диапазоны путей по корням для проверки «внутри папки» бинарным поиском.
    /// </summary>
    private sealed class ScopeSet
    {
        private readonly Dictionary<long, (string[] From, string[] To)> _byRoot = [];

        public ScopeSet(List<(long RootId, string From, string To)> sortedDisjoint)
        {
            foreach (var group in sortedDisjoint.GroupBy(static range => range.RootId))
            {
                _byRoot[group.Key] = (group.Select(static range => range.From).ToArray(), group.Select(static range => range.To).ToArray());
            }
        }

        public bool Contains(long rootId, string relPath)
        {
            if (!_byRoot.TryGetValue(rootId, out var ranges))
            {
                return false;
            }

            var index = Array.BinarySearch(ranges.From, relPath, StringComparer.Ordinal);
            if (index < 0)
            {
                index = ~index - 1;
            }

            return index >= 0 && string.CompareOrdinal(relPath, ranges.To[index]) < 0;
        }
    }

    private static void FillScopeTable(SqliteConnection connection, string table, List<(long RootId, string From, string To)> ranges)
    {
        using (var create = connection.CreateCommand())
        {
            create.CommandText = $"""
                CREATE TEMP TABLE IF NOT EXISTS {table} (root_id INTEGER NOT NULL, from_ TEXT NOT NULL, to_ TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS temp.{table}_ix ON {table} (root_id, from_);
                DELETE FROM temp.{table};
                """;
            create.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = $"INSERT INTO temp.{table} (root_id, from_, to_) VALUES ($root, $from, $to)";
        var root = insert.Parameters.Add("$root", SqliteType.Integer);
        var from = insert.Parameters.Add("$from", SqliteType.Text);
        var to = insert.Parameters.Add("$to", SqliteType.Text);
        insert.Prepare();
        foreach (var range in ranges)
        {
            root.Value = range.RootId;
            from.Value = range.From;
            to.Value = range.To;
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Записи тега в памяти (этап 19.5): проверка тега по идентификатору записи, без чтения её строки. Строится перед первым
    /// запросом, которому нужна, — чтение из источника тега и точный счёт обходятся без неё.
    /// </summary>
    /// <param name="function">Имя функции проверки в SQL.</param>
    /// <param name="sql">Запрос идентификаторов записей тега; повторы допустимы.</param>
    /// <param name="count">Сколько записей у тега.</param>
    /// <param name="key">Ключ состава тега: одинаковый у запросов, которым подходит одно и то же множество.</param>
    /// <param name="loads">Построения множеств, идущие сейчас.</param>
    internal sealed class TagMembers(string function, string sql, long count, string key, MemberLoads loads) : IDisposable
    {
        private MemberSet? _set;
        private SqliteConnection? _connection;

        /// <summary>Имя функции проверки в SQL.</summary>
        public string Function { get; } = function;

        /// <summary>
        /// Регистрирует функцию проверки на соединении. Напрямую через SQLitePCL, без обёртки Microsoft.Data.Sqlite: функция
        /// вызывается для каждой записи обхода индекса, и обёртка (чтение аргументов, упаковка результата) стоила втрое
        /// дороже самой проверки.
        /// </summary>
        public void Register(SqliteConnection connection)
        {
            SQLitePCL.raw.sqlite3_create_function(connection.Handle, Function, 1, SQLitePCL.raw.SQLITE_UTF8 | SQLitePCL.raw.SQLITE_DETERMINISTIC, null, Check);
            _connection = connection;
        }

        /// <summary>Снимает функцию проверки с соединения.</summary>
        public void Dispose()
        {
            if (_connection?.Handle is { } handle)
            {
                SQLitePCL.raw.sqlite3_create_function(handle, Function, 1, SQLitePCL.raw.SQLITE_UTF8 | SQLitePCL.raw.SQLITE_DETERMINISTIC, null, (SQLitePCL.delegate_function_scalar?)null);
            }

            _connection = null;
        }

        /// <summary>
        /// Строит множество, если оно ещё не построено, или берёт построенное параллельным запросом того же тега (подсчёт и
        /// выборка идут одновременно); отмена прерывает запрос.
        /// </summary>
        public void Load(SqliteConnection connection, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken)
            => _set ??= loads.GetOrLoad(key, () => MemberSet.Read(connection, sql, parameters, count, cancellationToken), cancellationToken);

        private void Check(SQLitePCL.sqlite3_context context, object userData, SQLitePCL.sqlite3_value[] args)
        {
            if (_set is not { } set)
            {
                SQLitePCL.raw.sqlite3_result_error(context, "Множество записей тега не построено.");
                return;
            }

            SQLitePCL.raw.sqlite3_result_int(context, set.Contains(SQLitePCL.raw.sqlite3_value_int64(args[0])) ? 1 : 0);
        }
    }

    /// <summary>
    /// Идентификаторы записей тега: битовая карта от наименьшего идентификатора или, если записи немногочисленны и далеко
    /// разнесены, хеш-множество.
    /// </summary>
    internal sealed class MemberSet
    {
        /// <summary>Битовая карта не больше этого числа слов даже у тега с немногими далеко разнесёнными записями (128 КБ).</summary>
        private const long MinBitmapWords = 16_384;

        private readonly ulong[]? _bits;
        private readonly long _offset;
        private readonly HashSet<long>? _ids;

        private MemberSet(ulong[]? bits, long offset, HashSet<long>? ids)
        {
            _bits = bits;
            _offset = offset;
            _ids = ids;
        }

        /// <summary>Есть ли запись в теге.</summary>
        public bool Contains(long id)
        {
            if (_bits is { } bits)
            {
                var index = id - _offset;
                return (ulong)index < (ulong)bits.Length * 64 && (bits[index >> 6] & (1UL << (int)(index & 63))) != 0;
            }

            return _ids!.Contains(id);
        }

        /// <summary>Читает идентификаторы запросом <paramref name="sql"/>.</summary>
        public static MemberSet Read(SqliteConnection connection, string sql, IReadOnlyDictionary<string, object?> parameters, long count, CancellationToken cancellationToken)
        {
            // Идентификаторы приходят одной строкой: чтение ридером по строке вдвое дороже, чем group_concat и разбор.
            cancellationToken.ThrowIfCancellationRequested();
            using var command = Command(connection, $"SELECT MIN(id), MAX(id), group_concat(id) FROM ({sql})", parameters);
            using var registration = cancellationToken.Register(() => Interrupt(connection));
            cancellationToken.ThrowIfCancellationRequested();
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(0))
            {
                return new MemberSet([], 0, null);
            }

            var min = reader.GetInt64(0);
            var words = ((reader.GetInt64(1) - min) / 64) + 1;
            var text = reader.GetString(2);
            if (words > Math.Max(count, MinBitmapWords))
            {
                // Немного записей, далеко разнесённых по идентификаторам: карта вышла бы больше множества.
                var ids = new HashSet<long>((int)Math.Min(count, int.MaxValue));
                foreach (var id in Parse(text))
                {
                    ids.Add(id);
                }

                return new MemberSet(null, 0, ids);
            }

            var bits = new ulong[words];
            foreach (var id in Parse(text))
            {
                var index = id - min;
                bits[index >> 6] |= 1UL << (int)(index & 63);
            }

            return new MemberSet(bits, min, null);
        }

        private static IEnumerable<long> Parse(string text)
        {
            long current = 0;
            foreach (var symbol in text)
            {
                if (symbol == ',')
                {
                    yield return current;
                    current = 0;
                }
                else
                {
                    current = (current * 10) + (symbol - '0');
                }
            }

            yield return current;
        }
    }

    /// <summary>
    /// Построения множеств записей тега, идущие сейчас: второй запрос того же тега ждёт первого, а не читает базу ещё раз.
    /// Готовое множество не хранится — следующий запрос после завершения строит его заново по текущему индексу.
    /// </summary>
    internal sealed class MemberLoads
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, Task<MemberSet>> _running = new(StringComparer.Ordinal);

        /// <summary>Строит множество или ждёт идущее построение с тем же ключом; если оно не удалось — строит само.</summary>
        public MemberSet GetOrLoad(string key, Func<MemberSet> load, CancellationToken cancellationToken)
        {
            TaskCompletionSource<MemberSet>? own = null;
            Task<MemberSet>? running;
            lock (_gate)
            {
                if (!_running.TryGetValue(key, out running))
                {
                    own = new TaskCompletionSource<MemberSet>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _running[key] = own.Task;
                }
            }

            if (own is null)
            {
                try
                {
                    return running!.WaitAsync(cancellationToken).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Первый запрос отменили или прервали посередине — строим сами.
                    return load();
                }
            }

            try
            {
                var set = load();
                own.SetResult(set);
                return set;
            }
            catch
            {
                own.SetCanceled(CancellationToken.None);
                throw;
            }
            finally
            {
                lock (_gate)
                {
                    _running.Remove(key);
                }
            }
        }
    }

    /// <summary>
    /// Условия и параметры запроса; текст условий содержит только имена параметров.
    /// </summary>
    internal sealed class Conditions
    {
        /// <summary>Условия, соединяемые через AND.</summary>
        public List<string> Clauses { get; } = [];

        /// <summary>Значения параметров.</summary>
        public Dictionary<string, object?> Parameters { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Источник кандидатов для запросов только из фильтров: «диапазоны папок ⋈ записи» у <c>in:</c>, записи с тегом и папки с
        /// авто-тегом у <c>tag:</c>; иначе <c>null</c> — обход всех записей.
        /// </summary>
        public string? ScopeJoinSource { get; set; }

        /// <summary>
        /// Какое из <see cref="Clauses"/> источник выполняет сам: при чтении из источника оно не проверяется повторно;
        /// <c>null</c> — источника нет.
        /// </summary>
        public int? SourceClauseIndex { get; set; }

        /// <summary>Кандидатов источника немного: выдачу «новые сверху» сортировать, а не обходить индекс времени изменения.</summary>
        public bool SortSourceByTime { get; set; }

        /// <summary>
        /// Читать не из источника и без подсказки индекса: индекс выбирает планировщик SQLite по остальным условиям, а тег
        /// проверяется по множеству <see cref="Members"/>.
        /// </summary>
        public bool PlannerChoosesIndex { get; set; }

        /// <summary>Сколько записей в источнике тега, точно; <c>null</c> — источник не от тега.</summary>
        public long? SourceCount { get; set; }

        /// <summary>Сколько записей подходит под все условия, если это известно без запроса (только тег).</summary>
        public long? KnownCount { get; set; }

        /// <summary>Множество записей тега для проверки по идентификатору; <c>null</c> — тег проверяется по пути.</summary>
        public TagMembers? Members { get; set; }

        /// <inheritdoc />
        public override string ToString()
        {
            var builder = new StringBuilder();
            foreach (var clause in Clauses)
            {
                builder.Append(clause).Append(" AND ");
            }

            return builder.ToString();
        }
    }
}

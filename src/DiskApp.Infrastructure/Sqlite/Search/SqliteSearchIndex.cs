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

    /// <summary>
    /// Сколько времени тратить на подсчёт совпадений при дополнительных условиях; не успели — число «не меньше N».
    /// Выдача не ждёт дорогого точного счёта (FTS по частому слову плюс фильтр).
    /// </summary>
    public TimeSpan CountTimeout { get; init; } = TimeSpan.FromMilliseconds(20);

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

            using var timeout = new CancellationTokenSource(query.Timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var collected = new List<SearchCandidate>(Math.Min(query.Limit, 1024));
            var partial = false;
            try
            {
                // Только фильтры: если совпадений больше лимита, сортировка всех по времени дорога — вместо неё
                // обходим индекс времени изменения с конца и останавливаемся на лимите.
                var newestFirst = plan.Source == CandidateSource.Filters
                    && CountUpTo(connection, plan, conditions, query.Limit + 1L, linked.Token) > query.Limit;
                Read(connection, MainSql(plan, conditions, query.Limit, newestFirst), conditions.Parameters, collected, linked.Token);
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

            try
            {
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
            var looser = plan with { FtsTerms = [], ShortTerms = [] };
            var conditions = BuildConditions(connection, looser);
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

        var source = plan.Source != CandidateSource.Filters ? Source(plan)
            : newestFirst ? "entries AS e INDEXED BY ix_entries_modified"
            : conditions.ScopeJoinSource ?? Source(plan);
        return $"SELECT {Columns} FROM {source} {Joins} WHERE {Where(plan, conditions)} {order} LIMIT $limit";
    }

    private static string Source(SearchPlan plan)
        => plan.Source == CandidateSource.FullText ? "entries_fts JOIN entries AS e ON e.id = entries_fts.rowid" : "entries AS e";

    /// <summary>
    /// Сколько записей подходит под план, но не больше <paramref name="upTo"/>: подзапрос с LIMIT останавливает обход.
    /// </summary>
    private static long CountUpTo(SqliteConnection connection, SearchPlan plan, Conditions conditions, long upTo, CancellationToken cancellationToken)
    {
        conditions.Parameters["$count_limit"] = upTo;
        var source = plan.Source == CandidateSource.Filters && conditions.ScopeJoinSource is { } join ? join : Source(plan);
        return Scalar(connection, $"SELECT COUNT(*) FROM (SELECT 1 FROM {source} WHERE {Where(plan, conditions)} LIMIT $count_limit)", conditions.Parameters, cancellationToken);
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
        var source = plan.Source == CandidateSource.Filters && conditions.ScopeJoinSource is { } join ? join : Source(plan);
        using var command = Command(connection, $"SELECT 1 FROM {source} WHERE {Where(plan, conditions)} LIMIT $count_limit", conditions.Parameters);
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

    private static string Where(SearchPlan plan, Conditions conditions)
    {
        var clauses = new List<string>(conditions.Clauses);
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
    private Conditions? BuildConditions(SqliteConnection connection, SearchPlan plan)
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

        for (var i = 0; i < plan.IncludeTags.Count; i++)
        {
            conditions.Parameters[$"$it{i}"] = plan.IncludeTags[i];
            conditions.Clauses.Add($"EXISTS (SELECT 1 FROM entry_tags AS et JOIN tags AS t ON t.id = et.tag_id WHERE et.entry_id = e.id AND t.name_folded = $it{i})");
        }

        for (var i = 0; i < plan.ExcludeTags.Count; i++)
        {
            conditions.Parameters[$"$et{i}"] = plan.ExcludeTags[i];
            conditions.Clauses.Add($"NOT EXISTS (SELECT 1 FROM entry_tags AS et JOIN tags AS t ON t.id = et.tag_id WHERE et.entry_id = e.id AND t.name_folded = $et{i})");
        }

        if (plan.Favorite is { } favorite)
        {
            conditions.Clauses.Add(favorite ? "EXISTS (SELECT 1 FROM favorites AS f2 WHERE f2.entry_id = e.id)" : "NOT EXISTS (SELECT 1 FROM favorites AS f2 WHERE f2.entry_id = e.id)");
        }

        return conditions;
    }

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
        foreach (var scope in scopes)
        {
            if (scope.FolderName is { } folderName)
            {
                using var command = Command(
                    connection,
                    "SELECT root_id, rel_path FROM entries WHERE name_folded = $name AND is_dir = 1",
                    new Dictionary<string, object?> { ["$name"] = folderName });
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var relPath = reader.GetString(1);
                    ranges.Add((reader.GetInt64(0), relPath + separator, relPath + (char)(separator + 1)));
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
    /// Условия и параметры запроса; текст условий содержит только имена параметров.
    /// </summary>
    internal sealed class Conditions
    {
        /// <summary>Условия, соединяемые через AND.</summary>
        public List<string> Clauses { get; } = [];

        /// <summary>Значения параметров.</summary>
        public Dictionary<string, object?> Parameters { get; } = new(StringComparer.Ordinal);

        /// <summary>Источник «диапазоны папок ⋈ записи» для запросов только из фильтров с <c>in:</c>; иначе <c>null</c>.</summary>
        public string? ScopeJoinSource { get; set; }

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

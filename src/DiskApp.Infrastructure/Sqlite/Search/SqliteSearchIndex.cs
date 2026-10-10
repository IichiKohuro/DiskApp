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

    private const int MaxResolvedFolders = 100;
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
                Read(connection, MainSql(plan, conditions, query.Limit), conditions.Parameters, collected, linked.Token);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == SqliteInterrupt)
            {
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

    /// <inheritdoc />
    public Task<long> CountAsync(SearchPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return Task.Run(() =>
        {
            using var connection = database.Connections.Open();
            var conditions = BuildConditions(connection, plan);
            if (conditions is null)
            {
                return 0L;
            }

            using var command = Command(connection, $"SELECT COUNT(*) FROM {Source(plan)} WHERE {Where(plan, conditions)}", conditions.Parameters);
            using var registration = cancellationToken.Register(() => Interrupt(connection));
            try
            {
                return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
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
    internal static string MainSql(SearchPlan plan, Conditions conditions, int limit)
    {
        conditions.Parameters["$limit"] = limit;
        var order = plan.Source switch
        {
            CandidateSource.NamePrefix => "ORDER BY e.name_folded",
            CandidateSource.Filters => "ORDER BY e.modified_utc DESC",
            _ => string.Empty,
        };

        return $"SELECT {Columns} FROM {Source(plan)} {Joins} WHERE {Where(plan, conditions)} {order} LIMIT $limit";
    }

    private static string Source(SearchPlan plan)
        => plan.Source == CandidateSource.FullText ? "entries_fts JOIN entries AS e ON e.id = entries_fts.rowid" : "entries AS e";

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
                    CreatedUtc = SqliteTime.Parse(reader.GetString(7)),
                    ModifiedUtc = SqliteTime.Parse(reader.GetString(8)),
                    Attributes = (FileAttributes)reader.GetInt64(9),
                    RelPath = reader.GetString(10),
                    Kind = (FileKind)reader.GetInt64(11),
                },
                reader.IsDBNull(12) ? 0 : reader.GetInt32(12),
                reader.IsDBNull(13) ? null : SqliteTime.Parse(reader.GetString(13)),
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
            var clause = FolderClause(connection, plan.IncludeFolders, conditions, "$if");
            if (clause is null)
            {
                return null;
            }

            conditions.Clauses.Add(clause);
        }

        if (plan.ExcludeFolders.Count > 0 && FolderClause(connection, plan.ExcludeFolders, conditions, "$ef") is { } excluded)
        {
            conditions.Clauses.Add($"NOT ({excluded})");
        }

        AddRanges(conditions, plan.IncludeSizes.Select(static range => ((object?)range.Min, (object?)(range.Max == long.MaxValue ? null : range.Max))).ToArray(), "e.size", "$is", inclusiveMax: true, negate: false, filesOnly: true);
        AddRanges(conditions, plan.ExcludeSizes.Select(static range => ((object?)range.Min, (object?)(range.Max == long.MaxValue ? null : range.Max))).ToArray(), "e.size", "$es", inclusiveMax: true, negate: true, filesOnly: true);
        AddRanges(conditions, Dates(plan.IncludeModified), "e.modified_utc", "$im", inclusiveMax: false, negate: false, filesOnly: false);
        AddRanges(conditions, Dates(plan.ExcludeModified), "e.modified_utc", "$em", inclusiveMax: false, negate: true, filesOnly: false);
        AddRanges(conditions, Dates(plan.IncludeCreated), "e.created_utc", "$ic", inclusiveMax: false, negate: false, filesOnly: false);
        AddRanges(conditions, Dates(plan.ExcludeCreated), "e.created_utc", "$ec", inclusiveMax: false, negate: true, filesOnly: false);

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
        => ranges.Select(static range => ((object?)SqliteTime.Format(range.FromUtc), (object?)SqliteTime.Format(range.ToUtc))).ToArray();

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

    private static void AddRanges(Conditions conditions, (object? From, object? To)[] ranges, string column, string prefix, bool inclusiveMax, bool negate, bool filesOnly)
    {
        if (ranges.Length == 0)
        {
            return;
        }

        var alternatives = new List<string>();
        for (var i = 0; i < ranges.Length; i++)
        {
            var parts = new List<string>();
            if (filesOnly)
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

    private string? FolderClause(SqliteConnection connection, IReadOnlyList<FolderScope> scopes, Conditions conditions, string prefix)
    {
        var alternatives = new List<string>();
        var index = 0;
        foreach (var scope in scopes)
        {
            if (scope.FolderName is { } folderName)
            {
                using var command = Command(
                    connection,
                    "SELECT root_id, rel_path FROM entries WHERE name_folded = $name AND is_dir = 1 LIMIT $limit",
                    new Dictionary<string, object?> { ["$name"] = folderName, ["$limit"] = MaxResolvedFolders });
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    alternatives.Add(Range(reader.GetInt64(0), reader.GetString(1), conditions, $"{prefix}{index++}", includeSelf: false));
                }
            }
            else if (scope.RootId is { } rootId)
            {
                alternatives.Add(string.IsNullOrEmpty(scope.RelPath)
                    ? Root(rootId, conditions, $"{prefix}{index++}")
                    : Range(rootId, scope.RelPath, conditions, $"{prefix}{index++}", includeSelf: false));
            }
        }

        return alternatives.Count == 0 ? null : "(" + string.Join(" OR ", alternatives) + ")";
    }

    private static string Root(long rootId, Conditions conditions, string name)
    {
        conditions.Parameters[name] = rootId;
        return $"e.root_id = {name}";
    }

    private string Range(long rootId, string relPath, Conditions conditions, string name, bool includeSelf)
    {
        var separator = _paths.Separator;
        conditions.Parameters[name + "r"] = rootId;
        conditions.Parameters[name + "f"] = relPath + separator;
        conditions.Parameters[name + "t"] = relPath + (char)(separator + 1);
        var descendants = $"(e.root_id = {name}r AND e.rel_path >= {name}f AND e.rel_path < {name}t)";
        if (!includeSelf)
        {
            return descendants;
        }

        conditions.Parameters[name + "s"] = relPath;
        return $"({descendants} OR (e.root_id = {name}r AND e.rel_path = {name}s))";
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

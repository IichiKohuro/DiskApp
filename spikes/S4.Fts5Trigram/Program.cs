// Спайк S4: SQLite FTS5 trigram на 1 млн синтетических записей.
// Вопросы: скорость пакетной вставки, размер БД (FTS по имени vs по имени и пути),
// задержка поиска для 2, 3 и 5 символов, сравнение с LIKE, кириллица.

using System.Diagnostics;
using System.Globalization;
using System.Text;

using Microsoft.Data.Sqlite;

var rows = 1_000_000;
var directory = Path.Combine(Path.GetTempPath(), "DiskApp.S4");
foreach (var arg in args)
{
    if (arg.StartsWith("rows=")) rows = int.Parse(arg[5..], CultureInfo.InvariantCulture);
    if (arg.StartsWith("dir=")) directory = arg[4..];
}

Directory.CreateDirectory(directory);
Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine($"S4: {rows:N0} записей, папка {directory}, SQLite {SqliteVersion()}");
Console.WriteLine();

var report = new StringBuilder();
report.AppendLine($"Записей: {rows:N0}; SQLite {SqliteVersion()}; машина: {Environment.MachineName}, {Environment.ProcessorCount} CPU");
report.AppendLine();

var layouts = new[]
{
    new Layout("name", "FTS по имени", "name"),
    new Layout("name-path", "FTS по имени и пути", "name, path"),
};

foreach (var layout in layouts)
{
    var file = Path.Combine(directory, $"s4-{layout.Key}.db");
    File.Delete(file);
    File.Delete(file + "-wal");
    File.Delete(file + "-shm");

    using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString());
    connection.Open();
    Execute(connection, "PRAGMA journal_mode = WAL");
    Execute(connection, "PRAGMA synchronous = NORMAL");
    Execute(connection, "PRAGMA cache_size = -65536");
    Execute(connection, "PRAGMA temp_store = MEMORY");
    Execute(connection, "CREATE TABLE entries(id INTEGER PRIMARY KEY, name TEXT NOT NULL, path TEXT NOT NULL, size INTEGER NOT NULL, mtime INTEGER NOT NULL)");
    Execute(connection, $"CREATE VIRTUAL TABLE entries_fts USING fts5({layout.FtsColumns}, content='entries', content_rowid='id', tokenize='trigram')");

    var generator = new NameGenerator(seed: 42);
    var insertWatch = Stopwatch.StartNew();
    const int batch = 20_000;
    var transaction = connection.BeginTransaction();
    {
        using var insertEntry = connection.CreateCommand();
        insertEntry.Transaction = transaction;
        insertEntry.CommandText = "INSERT INTO entries(id, name, path, size, mtime) VALUES ($id, $name, $path, $size, $mtime)";
        var pId = insertEntry.Parameters.Add("$id", SqliteType.Integer);
        var pName = insertEntry.Parameters.Add("$name", SqliteType.Text);
        var pPath = insertEntry.Parameters.Add("$path", SqliteType.Text);
        var pSize = insertEntry.Parameters.Add("$size", SqliteType.Integer);
        var pMtime = insertEntry.Parameters.Add("$mtime", SqliteType.Integer);

        using var insertFts = connection.CreateCommand();
        insertFts.Transaction = transaction;
        insertFts.CommandText = layout.FtsColumns.Contains("path")
            ? "INSERT INTO entries_fts(rowid, name, path) VALUES ($id, $name, $path)"
            : "INSERT INTO entries_fts(rowid, name) VALUES ($id, $name)";
        var fId = insertFts.Parameters.Add("$id", SqliteType.Integer);
        var fName = insertFts.Parameters.Add("$name", SqliteType.Text);
        var fPath = layout.FtsColumns.Contains("path") ? insertFts.Parameters.Add("$path", SqliteType.Text) : null;

        for (var i = 1; i <= rows; i++)
        {
            var (name, path, size, mtime) = generator.Next(i);
            pId.Value = i; pName.Value = name; pPath.Value = path; pSize.Value = size; pMtime.Value = mtime;
            insertEntry.ExecuteNonQuery();
            fId.Value = i; fName.Value = name; if (fPath is not null) fPath.Value = path;
            insertFts.ExecuteNonQuery();

            if (i % batch == 0)
            {
                transaction.Commit();
                transaction.Dispose();
                transaction = connection.BeginTransaction();
                insertEntry.Transaction = transaction;
                insertFts.Transaction = transaction;
                if (i % 200_000 == 0) Console.WriteLine($"  [{layout.Key}] вставлено {i:N0} за {insertWatch.Elapsed.TotalSeconds:F1} с");
            }
        }

        transaction.Commit();
        transaction.Dispose();
    }

    insertWatch.Stop();
    var optimizeWatch = Stopwatch.StartNew();
    Execute(connection, "INSERT INTO entries_fts(entries_fts) VALUES('optimize')");
    optimizeWatch.Stop();
    Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE)");
    var sizeBytes = new FileInfo(file).Length;
    var ftsBytes = PageBytes(connection, "entries_fts%");
    var tableBytes = PageBytes(connection, "entries");

    Console.WriteLine($"[{layout.Title}] вставка {rows:N0} за {insertWatch.Elapsed.TotalSeconds:F1} с = {rows / insertWatch.Elapsed.TotalSeconds:N0} строк/с; optimize {optimizeWatch.Elapsed.TotalSeconds:F1} с; файл {sizeBytes / 1048576.0:F0} МБ (таблица ~{tableBytes / 1048576.0:F0} МБ, FTS ~{ftsBytes / 1048576.0:F0} МБ)");
    report.AppendLine($"## {layout.Title}");
    report.AppendLine();
    report.AppendLine($"- Вставка {rows:N0} строк пакетами по {batch:N0}: {insertWatch.Elapsed.TotalSeconds:F1} с ({rows / insertWatch.Elapsed.TotalSeconds:N0} строк/с), optimize {optimizeWatch.Elapsed.TotalSeconds:F1} с");
    report.AppendLine($"- Размер файла: {sizeBytes / 1048576.0:F0} МБ (таблица entries ~{tableBytes / 1048576.0:F0} МБ, индекс FTS ~{ftsBytes / 1048576.0:F0} МБ)");
    report.AppendLine();
    report.AppendLine("| Запрос | Способ | Совпадений | Первые 50, мс (медиана) | COUNT(*), мс |");
    report.AppendLine("|---|---|---|---|---|");

    var queries = new[] { "re", "rep", "report", "от", "отч", "отчёт", "20", "202", "2024_", "x7", "zq", "invoice 2023", "фото отпуск" };
    foreach (var query in queries)
    {
        var scenarios = new List<(string Way, string Sql, string Parameter)>();
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.All(t => t.Length >= 3))
        {
            var match = string.Join(" ", tokens.Select(t => "\"" + t.Replace("\"", "\"\"") + "\""));
            scenarios.Add(("FTS MATCH", "SELECT rowid FROM entries_fts WHERE entries_fts MATCH $q ORDER BY rank LIMIT 50", match));
        }

        if (tokens.Length == 1)
        {
            scenarios.Add(("FTS LIKE", "SELECT rowid FROM entries_fts WHERE name LIKE $q LIMIT 50", "%" + query + "%"));
            scenarios.Add(("LIKE по таблице", "SELECT id FROM entries WHERE name LIKE $q LIMIT 50", "%" + query + "%"));
        }

        foreach (var (way, sql, parameter) in scenarios)
        {
            var countSql = sql.Replace("SELECT rowid FROM", "SELECT COUNT(*) FROM").Replace("SELECT id FROM", "SELECT COUNT(*) FROM").Replace(" ORDER BY rank", "").Replace(" LIMIT 50", "");
            var first = Measure(connection, sql, parameter, repeat: 5, out var matches);
            var count = Measure(connection, countSql, parameter, repeat: 3, out var total);
            Console.WriteLine($"  {query,-14} {way,-16} совпадений {total,9:N0}  первые 50: {first,8:F1} мс  count: {count,8:F1} мс");
            report.AppendLine($"| `{query}` | {way} | {total:N0} | {first:F1} | {count:F1} |");
        }
    }

    report.AppendLine();
    Console.WriteLine();
}

var resultFile = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "results.md");
File.WriteAllText(Path.GetFullPath(resultFile), report.ToString(), new UTF8Encoding(false));
Console.WriteLine($"Отчёт: {Path.GetFullPath(resultFile)}");

static string SqliteVersion()
{
    using var c = new SqliteConnection("Data Source=:memory:");
    c.Open();
    using var cmd = c.CreateCommand();
    cmd.CommandText = "SELECT sqlite_version()";
    return (string)cmd.ExecuteScalar()!;
}

static void Execute(SqliteConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    command.ExecuteNonQuery();
}

static long PageBytes(SqliteConnection connection, string namePattern)
{
    // dbstat доступен в сборках Microsoft.Data.Sqlite (SQLITE_ENABLE_DBSTAT_VTAB); иначе вернёт 0.
    try
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(pgsize), 0) FROM dbstat WHERE name LIKE $n";
        command.Parameters.AddWithValue("$n", namePattern);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
    catch (SqliteException)
    {
        return 0;
    }
}

static double Measure(SqliteConnection connection, string sql, string parameter, int repeat, out long resultCount)
{
    var times = new List<double>();
    resultCount = 0;
    for (var i = 0; i < repeat; i++)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$q", parameter);
        var watch = Stopwatch.StartNew();
        using var reader = command.ExecuteReader();
        long n = 0;
        var isCount = sql.StartsWith("SELECT COUNT", StringComparison.Ordinal);
        while (reader.Read())
        {
            n = isCount ? reader.GetInt64(0) : n + 1;
        }

        watch.Stop();
        times.Add(watch.Elapsed.TotalMilliseconds);
        resultCount = n;
    }

    times.Sort();
    return times[times.Count / 2];
}

sealed record Layout(string Key, string Title, string FtsColumns);

/// <summary>Синтетические имена и пути: латиница и кириллица, расширения по реальному распределению.</summary>
sealed class NameGenerator
{
    private static readonly string[] LatinWords =
    [
        "report", "invoice", "budget", "photo", "screenshot", "backup", "draft", "final", "notes", "project", "design", "logo", "presentation",
        "contract", "agreement", "summary", "plan", "roadmap", "spec", "readme", "license", "config", "settings", "index", "main", "program",
        "service", "client", "server", "test", "data", "export", "import", "archive", "old", "new", "copy", "scan", "resume", "letter", "memo",
        "agenda", "minutes", "lecture", "homework", "thesis", "chapter", "video", "audio", "music", "song", "track", "album", "movie", "clip",
        "image", "picture", "wallpaper", "icon", "sketch", "diagram", "chart", "table", "sheet", "list", "form", "template", "sample", "demo",
        "release", "build", "setup", "installer", "update", "patch", "driver", "manual", "guide", "tutorial", "course", "lesson", "quiz",
        "family", "vacation", "trip", "wedding", "birthday", "party", "kids", "garden", "house", "car", "bike", "camera", "phone", "laptop",
    ];

    private static readonly string[] CyrillicWords =
    [
        "отчёт", "счёт", "договор", "бюджет", "фото", "скриншот", "резервная", "черновик", "итог", "заметки", "проект", "дизайн", "презентация",
        "соглашение", "сводка", "план", "спецификация", "настройки", "данные", "экспорт", "архив", "старый", "новый", "копия", "скан", "резюме",
        "письмо", "повестка", "протокол", "лекция", "домашка", "диплом", "глава", "видео", "музыка", "песня", "фильм", "картинка", "обои",
        "схема", "таблица", "список", "форма", "шаблон", "пример", "релиз", "сборка", "установка", "обновление", "руководство", "курс", "урок",
        "семья", "отпуск", "поездка", "свадьба", "день_рождения", "дети", "дача", "дом", "машина", "камера", "телефон", "ноутбук", "квартал",
    ];

    private static readonly (string Ext, int Weight)[] Extensions =
    [
        ("jpg", 25), ("png", 10), ("pdf", 10), ("docx", 8), ("xlsx", 5), ("txt", 5), ("mp4", 4), ("mp3", 4), ("zip", 3), ("cs", 6), ("json", 4),
        ("md", 3), ("pptx", 2), ("exe", 1), ("msi", 1), ("dll", 3), ("html", 3), ("css", 2), ("js", 3), ("", 2),
    ];

    private static readonly string[] Roots = [@"D:\Projects", @"C:\Users\Иван\Documents", @"C:\Users\Иван\Downloads", @"E:\Photos"];

    private readonly Random _random;
    private readonly int _extensionWeightTotal = Extensions.Sum(e => e.Weight);

    public NameGenerator(int seed) => _random = new Random(seed);

    public (string Name, string Path, long Size, long Mtime) Next(int index)
    {
        var cyrillic = _random.NextDouble() < 0.3;
        var words = cyrillic ? CyrillicWords : LatinWords;
        var wordCount = _random.Next(1, 4);
        var separator = _random.Next(3) switch { 0 => " ", 1 => "_", _ => "-" };
        var parts = new List<string>();
        for (var i = 0; i < wordCount; i++) parts.Add(words[_random.Next(words.Length)]);
        if (_random.NextDouble() < 0.35) parts.Add((2019 + _random.Next(8)).ToString(CultureInfo.InvariantCulture));
        if (_random.NextDouble() < 0.25) parts.Add(_random.Next(1, 9999).ToString("D4", CultureInfo.InvariantCulture));
        if (_random.NextDouble() < 0.1) parts.Add("IMG_" + _random.Next(1000, 9999).ToString(CultureInfo.InvariantCulture));

        var name = string.Join(separator, parts);
        if (_random.NextDouble() < 0.15) name = char.ToUpperInvariant(name[0]) + name[1..];
        var extension = PickExtension();
        if (extension.Length > 0) name += "." + extension;

        var depth = _random.Next(1, 6);
        var segments = new List<string>();
        for (var i = 0; i < depth; i++) segments.Add((cyrillic ? CyrillicWords : LatinWords)[_random.Next(words.Length)]);
        var path = Roots[_random.Next(Roots.Length)] + "\\" + string.Join("\\", segments);

        var size = (long)(Math.Exp(_random.NextDouble() * 18) );
        var mtime = 1_600_000_000L + _random.Next(0, 200_000_000);
        return (name, path, size, mtime);
    }

    private string PickExtension()
    {
        var roll = _random.Next(_extensionWeightTotal);
        foreach (var (ext, weight) in Extensions)
        {
            if (roll < weight) return ext;
            roll -= weight;
        }

        return "bin";
    }
}

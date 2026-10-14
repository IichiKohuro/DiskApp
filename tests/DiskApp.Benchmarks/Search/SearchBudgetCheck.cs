using System.Diagnostics;
using System.Globalization;

using DiskApp.Core.Abstractions;
using DiskApp.Core.Domain;
using DiskApp.Core.Paths;
using DiskApp.Core.Search;
using DiskApp.Infrastructure.Sqlite;
using DiskApp.Infrastructure.Sqlite.Repositories;
using DiskApp.Infrastructure.Sqlite.Search;

namespace DiskApp.Benchmarks.Search;

/// <summary>
/// Проверка бюджета поиска (PLAN.md, 3.6): синтетическая база на 1 млн записей с фиксированным seed (имена из словаря
/// на русском и английском, годы, кварталы, номера, расширения, дерево папок, история открытий и избранное),
/// затем p50 и p95 полного <see cref="SearchService.SearchAsync"/> для запросов из 2, 3 и 5 символов, нескольких слов,
/// с фильтрами, пустого, только фильтров, с опечаткой и с <c>tag:</c> по авто-тегам папок (этап 19: 40 рабочих папок и целая
/// область на 5 % записей). Кэши сервиса сбрасываются перед каждым замером, поэтому
/// уточнение запроса не подыгрывает. Цель — p95 &lt; 50 мс.
/// Запуск: <c>dotnet run -c Release --project tests/DiskApp.Benchmarks -- search-budget [записей] [путь к базе]</c>;
/// если база по пути уже есть, она используется повторно.
/// </summary>
internal static class SearchBudgetCheck
{
    private const int Runs = 40;

    private static readonly string[] Words =
    [
        "report", "invoice", "budget", "contract", "presentation", "photo", "vacation", "summary", "notes", "draft",
        "final", "scan", "receipt", "plan", "project", "meeting", "agenda", "backup", "archive", "design",
        "logo", "specification", "manual", "readme", "config", "data", "export", "statement",
        "отчет", "счет", "договор", "бюджет", "презентация", "фото", "отпуск", "заметки", "черновик", "итог",
        "скан", "чек", "план", "проект", "встреча", "повестка", "резерв", "архив", "дизайн", "макет",
        "инструкция", "выгрузка", "выписка", "акт", "накладная", "протокол",
    ];

    private static readonly string[] Extensions = ["pdf", "docx", "xlsx", "jpg", "png", "txt", "md", "zip", "pptx", "csv", "mp4", "cs", "json", "log"];

    private static readonly string[] Queries =
    [
        string.Empty, "re", "от", "rep", "отч", "202", "repor", "отчет", "invoi",
        "report 2025", "отчет q3", "rep ext:pdf", "report modified:year", "in:2025 report", "in:2025",
        "type:image vacation", "ext:pdf", "size:>100mb", "reprot", "budget -draft ext:xlsx",
        "tag:тз", "tag:тз report", "tag:проект ext:pdf", "-tag:проект report", "tag:смета",
    ];

    public static async Task<int> RunAsync(int count, string? databasePath)
    {
        var temporary = databasePath is null;
        var path = databasePath ?? Path.Combine(Path.GetTempPath(), "DiskApp.Benchmarks", $"search-{count}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        try
        {
            await using var database = new SqliteDatabase(path);
            var initialization = await database.InitializeAsync();
            var clock = SystemClock.Instance;
            var roots = new SqliteRootRepository(database, clock);
            var entries = new SqliteFileIndexRepository(database);

            if ((await entries.CountAsync()) < count)
            {
                if (!initialization.WasCreated)
                {
                    Console.WriteLine("Существующая база меньше заданного размера — пересоздайте её, удалив файл.");
                    return 2;
                }

                await GenerateAsync(database, roots, entries, count);
            }

            await SeedAutoTagsAsync(database, roots);
            await database.OptimizeAsync();
            var size = new FileInfo(path).Length + (File.Exists(path + "-wal") ? new FileInfo(path + "-wal").Length : 0);
            var stored = await entries.CountAsync();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"База: {stored:N0} записей, {size / (1024 * 1024)} МБ ({path})"));

            var service = new SearchService(
                new SqliteSearchIndex(database),
                roots,
                new SqliteQueryHistoryRepository(database),
                new SqliteTagRepository(database),
                clock,
                PathNormalizer.Current);

            var allOk = true;
            Console.WriteLine($"{"Запрос",-28} {"p50, мс",8} {"p95, мс",8} {"макс",8} {"найдено",10}");
            foreach (var query in Queries)
            {
                var (p50, p95, max, total) = await MeasureAsync(service, query);
                allOk &= p95 < 50;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{(query.Length == 0 ? "(пустой)" : query),-28} {p50,8:F1} {p95,8:F1} {max,8:F1} {total,10:N0}{(p95 < 50 ? string.Empty : "  ← вне бюджета")}"));
            }

            await ProfileTagsAsync(new SqliteSearchIndex(database), await roots.GetAllAsync(), clock);

            var typing = await MeasureTypingAsync(service, ["r", "re", "rep", "repo", "repor", "report"]);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Набор «report» по буквам (с кэшем уточнения): {string.Join(" / ", typing.Select(static ms => ms.ToString("F1", CultureInfo.InvariantCulture)))} мс"));
            Console.WriteLine(allOk ? "Бюджет поиска выполнен: p95 < 50 мс для всех запросов." : "Бюджет поиска НЕ выполнен для части запросов.");
            return allOk ? 0 : 1;
        }
        finally
        {
            DiskApp.Infrastructure.Sqlite.SqliteConnectionFactory.ClearAllPools();
            if (temporary)
            {
                foreach (var file in new[] { path, path + "-wal", path + "-shm" })
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }
            }
        }
    }

    private static async Task<(double P50, double P95, double Max, long Total)> MeasureAsync(SearchService service, string query)
    {
        var samples = new List<double>(Runs);
        long total = 0;
        for (var run = 0; run <= Runs; run++)
        {
            service.InvalidateCaches();
            var stopwatch = Stopwatch.StartNew();
            var page = await service.SearchAsync(new SearchRequest { Text = query, Limit = 50 });
            stopwatch.Stop();
            total = page.TotalCount;
            if (run > 0)
            {
                samples.Add(stopwatch.Elapsed.TotalMilliseconds);
            }
        }

        samples.Sort();
        return (Percentile(samples, 0.5), Percentile(samples, 0.95), samples[^1], total);
    }

    /// <summary>
    /// Разбор запросов только из <c>tag:</c> по стадиям (этап 19.5): подсчёт и выборка кандидатов по отдельности, без
    /// параллельной работы сервиса, с теми же лимитами, что у <see cref="SearchService"/> по умолчанию.
    /// </summary>
    private static async Task ProfileTagsAsync(SqliteSearchIndex index, IReadOnlyList<Root> roots, SystemClock clock)
    {
        var options = new SearchOptions();
        Console.WriteLine($"{"Стадии tag:",-28} {"счёт p50",8} {"выборка",8} {"кандид.",10}");
        foreach (var text in Queries.Where(static query => query.StartsWith("tag:", StringComparison.Ordinal)))
        {
            var plan = SearchPlanner.Build(QueryParser.Parse(text), roots, PathNormalizer.Current, clock.UtcNow, TimeZoneInfo.Local);
            var counts = new List<double>(Runs);
            var finds = new List<double>(Runs);
            var found = 0;
            for (var run = 0; run <= Runs; run++)
            {
                var stopwatch = Stopwatch.StartNew();
                await index.CountAsync(plan, options.CountCap);
                var counted = stopwatch.Elapsed.TotalMilliseconds;
                stopwatch.Restart();
                var batch = await index.FindAsync(plan, new CandidateQuery(options.CandidateLimit, options.QueryTimeout));
                found = batch.Candidates.Count;
                if (run > 0)
                {
                    counts.Add(counted);
                    finds.Add(stopwatch.Elapsed.TotalMilliseconds);
                }
            }

            counts.Sort();
            finds.Sort();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{text,-28} {Percentile(counts, 0.5),8:F1} {Percentile(finds, 0.5),8:F1} {found,10:N0}"));
        }
    }

    private static async Task<List<double>> MeasureTypingAsync(SearchService service, string[] steps)
    {
        service.InvalidateCaches();
        var result = new List<double>();
        foreach (var step in steps)
        {
            var stopwatch = Stopwatch.StartNew();
            await service.SearchAsync(new SearchRequest { Text = step, Limit = 50 });
            result.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        return result;
    }

    /// <summary>
    /// Авто-теги папок (этап 19), как у пользователя: «проект» у 40 рабочих папок, «тз» у целой области (~5 % записей) и у
    /// пяти папок в других областях, «смета» у трёх папок (~0,6 %: записи тега сортируются, а не ищутся обходом индекса
    /// времени, этап 19.5). Повторный запуск задаёт те же наборы.
    /// </summary>
    private static async Task SeedAutoTagsAsync(SqliteDatabase database, SqliteRootRepository roots)
    {
        var tags = new SqliteTagRepository(database);
        var folderTags = new SqliteFolderTagRepository(database, PathNormalizer.Current);
        var project = await tags.AddAsync("Проект");
        var spec = await tags.AddAsync("ТЗ");
        var estimate = await tags.AddAsync("Смета");
        var root = (await roots.GetAllAsync())[0];
        var separator = PathNormalizer.Current.Separator;
        await folderTags.SetAsync($"{root.Path}{separator}area03", [spec.Id]);
        for (var area = 0; area < 20; area++)
        {
            for (var topic = 0; topic < 2; topic++)
            {
                var path = $"{root.Path}{separator}area{area:00}{separator}{Words[(area * 25 + topic) % Words.Length]}{topic:00}";
                await folderTags.SetAsync(path, area is >= 10 and < 15 && topic == 0 ? [project.Id, spec.Id] : [project.Id]);
            }
        }

        for (var area = 15; area < 18; area++)
        {
            await folderTags.SetAsync($"{root.Path}{separator}area{area:00}{separator}{Words[((area * 25) + 2) % Words.Length]}02", [estimate.Id]);
        }
    }

    private static double Percentile(List<double> sorted, double fraction)
        => sorted[Math.Clamp((int)Math.Ceiling(fraction * sorted.Count) - 1, 0, sorted.Count - 1)];

    private static async Task GenerateAsync(SqliteDatabase database, SqliteRootRepository roots, SqliteFileIndexRepository entries, int count)
    {
        Console.WriteLine($"Генерация {count:N0} записей (seed 42)…");
        var stopwatch = Stopwatch.StartNew();
        var random = new Random(42);
        var now = DateTimeOffset.UtcNow;
        var root = await roots.AddAsync(new Root { Path = OperatingSystem.IsWindows() ? @"D:\Bench" : "/bench", Label = "Bench" });
        var separator = PathNormalizer.Current.Separator;
        var folders = new List<string>();
        var batch = new List<FileEntry>(10_000);

        async Task FlushAsync()
        {
            if (batch.Count > 0)
            {
                await entries.UpsertAsync(batch, 1);
                batch.Clear();
            }
        }

        FileEntry Folder(string relPath) => FileEntry.Create(root.Id, null, relPath, true, 0, now.AddYears(-3), now.AddYears(-1), FileAttributes.Directory);

        for (var area = 0; area < 20; area++)
        {
            var areaPath = $"area{area:00}";
            batch.Add(Folder(areaPath));
            for (var topic = 0; topic < 25; topic++)
            {
                var topicPath = $"{areaPath}{separator}{Words[(area * 25 + topic) % Words.Length]}{topic:00}";
                batch.Add(Folder(topicPath));
                for (var year = 2019; year <= 2026; year++)
                {
                    var yearPath = $"{topicPath}{separator}{year}";
                    batch.Add(Folder(yearPath));
                    folders.Add(yearPath);
                }
            }
        }

        await FlushAsync();
        var created = folders.Count + 520;
        var used = new List<string>();
        for (var i = 0; created < count; i++, created++)
        {
            var name = Name(random, i);
            var folder = folders[random.Next(folders.Count)];
            var relPath = $"{folder}{separator}{name}";
            var size = (long)Math.Pow(10, 2 + (random.NextDouble() * 7));
            var modified = now.AddDays(-random.Next(0, 2_500)).AddSeconds(-random.Next(86_400));
            batch.Add(FileEntry.Create(root.Id, null, relPath, false, size, modified.AddDays(-random.Next(0, 100)), modified, FileAttributes.Archive));
            if (i % 500 == 0)
            {
                used.Add(relPath);
            }

            if (batch.Count >= 10_000)
            {
                await FlushAsync();
                if (created % 100_000 < 10_000)
                {
                    Console.WriteLine($"  {created:N0} за {stopwatch.Elapsed.TotalSeconds:F0} с");
                }
            }
        }

        await FlushAsync();

        var usage = new SqliteUsageRepository(database);
        var favorites = new SqliteFavoriteRepository(database);
        for (var i = 0; i < used.Count; i++)
        {
            var entry = await entries.GetByPathAsync(root.Id, used[i]);
            if (entry is null)
            {
                continue;
            }

            for (var opens = 0; opens <= i % 7; opens++)
            {
                await usage.RecordOpenAsync(entry.Id, now.AddDays(-(i % 90)));
            }

            if (i % 20 == 0)
            {
                await favorites.AddAsync(entry.Id, now);
            }
        }

        await roots.SetLastScanAsync(root.Id, now);
        Console.WriteLine($"Сгенерировано за {stopwatch.Elapsed.TotalSeconds:F0} с");
    }

    private static string Name(Random random, int index)
    {
        var first = Words[random.Next(Words.Length)];
        var second = random.Next(3) == 0 ? Words[random.Next(Words.Length)] : null;
        var year = 2019 + random.Next(8);
        var separators = new[] { " ", "_", "-" };
        var separator = separators[random.Next(separators.Length)];
        var parts = new List<string> { random.Next(4) == 0 ? char.ToUpperInvariant(first[0]) + first[1..] : first };
        if (second is not null)
        {
            parts.Add(second);
        }

        if (random.Next(2) == 0)
        {
            parts.Add(year.ToString(CultureInfo.InvariantCulture));
        }

        if (random.Next(4) == 0)
        {
            parts.Add("q" + (1 + random.Next(4)).ToString(CultureInfo.InvariantCulture));
        }

        parts.Add(index.ToString("x6", CultureInfo.InvariantCulture));
        return string.Join(separator, parts) + "." + Extensions[random.Next(Extensions.Length)];
    }
}

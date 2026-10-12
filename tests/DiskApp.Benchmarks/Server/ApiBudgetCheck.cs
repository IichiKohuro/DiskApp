using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;

using DiskApp.Contracts;
using DiskApp.Core.Search;
using DiskApp.Infrastructure;
using DiskApp.Server;

using Microsoft.Extensions.DependencyInjection;

namespace DiskApp.Benchmarks.Server;

/// <summary>
/// Проверка бюджета поиска через API (PLAN.md, 4.7 и раздел 8): сервер внутри процесса на базе из <c>search-budget</c>
/// (1 млн записей), запросы по loopback через <see cref="HttpClient"/> с разбором JSON.
/// <list type="number">
/// <item>Последовательно: те же запросы, что в <c>search-budget</c>, по 40 раз, кэши сервиса сброшены — цель p95 &lt; 60 мс.</item>
/// <item>Нагрузочный smoke: 100 одновременных поисков разными запросами, пять волн — p95 задержки, пропускная способность, ошибки.</item>
/// </list>
/// Запуск: <c>dotnet run -c Release --project tests/DiskApp.Benchmarks -- api-budget [путь к базе]</c>; по умолчанию —
/// <c>%TEMP%\DiskApp.Benchmarks\search-1000000.db</c> или <c>search-1m.db</c>.
/// </summary>
internal static class ApiBudgetCheck
{
    private const int Runs = 40;
    private const int Parallel = 100;
    private const int Waves = 5;

    private static readonly string[] DefaultDatabases = ["search-1000000.db", "search-1m.db"];

    private static readonly string[] Queries =
    [
        string.Empty, "re", "от", "rep", "отч", "202", "repor", "отчет", "invoi",
        "report 2025", "отчет q3", "rep ext:pdf", "report modified:year", "in:2025 report", "in:2025",
        "type:image vacation", "ext:pdf", "size:>100mb", "reprot", "budget -draft ext:xlsx",
    ];

    public static async Task<int> RunAsync(string? databasePath)
    {
        var benchDirectory = Path.Combine(Path.GetTempPath(), "DiskApp.Benchmarks");
        var source = databasePath
            ?? DefaultDatabases.Select(name => Path.Combine(benchDirectory, name)).FirstOrDefault(File.Exists);
        if (source is null || !File.Exists(source))
        {
            Console.WriteLine("Нет базы: сначала выполните search-budget (создаст базу на 1 млн записей).");
            return 2;
        }

        // Сервер открывает index.db в своей папке данных: база бенчмарка копируется туда один раз.
        var dataDirectory = Path.Combine(benchDirectory, "api-data");
        Directory.CreateDirectory(dataDirectory);
        var database = Path.Combine(dataDirectory, "index.db");
        if (!File.Exists(database) || new FileInfo(database).Length != new FileInfo(source).Length)
        {
            File.Copy(source, database, overwrite: true);
        }

        await using var host = DiskAppServerHost.Create(new DiskAppServerOptions { Paths = AppPaths.ForDataDirectory(dataDirectory), StartIndexer = false });
        await host.StartAsync();
        var service = host.Services.GetRequiredService<SearchService>();
        using var client = new HttpClient { BaseAddress = host.BaseAddress, Timeout = TimeSpan.FromSeconds(30) };
        Console.WriteLine($"Сервер {host.BaseAddress}, база {database} ({new FileInfo(database).Length / (1024 * 1024)} МБ)");

        // Прогрев: JIT, пул соединений SQLite, HTTP keep-alive.
        foreach (var query in Queries)
        {
            await SearchAsync(client, query);
        }

        var allOk = true;
        Console.WriteLine($"{"Запрос",-28} {"p50, мс",8} {"p95, мс",8} {"макс",8} {"найдено",10}");
        foreach (var query in Queries)
        {
            var samples = new List<double>(Runs);
            long total = 0;
            for (var run = 0; run < Runs; run++)
            {
                service.InvalidateCaches();
                var stopwatch = Stopwatch.StartNew();
                var response = await SearchAsync(client, query);
                samples.Add(stopwatch.Elapsed.TotalMilliseconds);
                total = response.Total;
            }

            samples.Sort();
            var p95 = Percentile(samples, 0.95);
            allOk &= p95 < 60;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{(query.Length == 0 ? "(пустой)" : query),-28} {Percentile(samples, 0.5),8:F1} {p95,8:F1} {samples[^1],8:F1} {total,10:N0}{(p95 < 60 ? string.Empty : "  ← вне бюджета")}"));
        }

        // Масштабирование самого сервиса (без HTTP): пропускная способность при разной степени параллелизма.
        foreach (var degree in new[] { 1, 2, 4, 8, 16 })
        {
            service.InvalidateCaches();
            var count = 0;
            var watch = Stopwatch.StartNew();
            await System.Threading.Tasks.Parallel.ForEachAsync(Enumerable.Range(0, 200), new ParallelOptions { MaxDegreeOfParallelism = degree }, async (i, ct) =>
            {
                await service.SearchAsync(new SearchRequest { Text = Queries[i % Queries.Length], Limit = 50 }, ct);
                Interlocked.Increment(ref count);
            });
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Сервис, параллелизм {degree,2}: {count / watch.Elapsed.TotalSeconds,6:F0} поисков/с"));
        }

        var latencies = new List<double>(Parallel * Waves);
        var errors = 0;
        var wall = Stopwatch.StartNew();
        for (var wave = 0; wave < Waves; wave++)
        {
            service.InvalidateCaches();
            var tasks = Enumerable.Range(0, Parallel).Select(async i =>
            {
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    await SearchAsync(client, Queries[(i + wave) % Queries.Length]);
                    return stopwatch.Elapsed.TotalMilliseconds;
                }
                catch (HttpRequestException)
                {
                    Interlocked.Increment(ref errors);
                    return double.NaN;
                }
            }).ToArray();
            latencies.AddRange((await Task.WhenAll(tasks)).Where(static ms => !double.IsNaN(ms)));
        }

        wall.Stop();
        latencies.Sort();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Нагрузка: {Waves} волн по {Parallel} одновременных поисков — p50 {Percentile(latencies, 0.5):F1} мс, p95 {Percentile(latencies, 0.95):F1} мс, "
            + $"макс {latencies[^1]:F1} мс, {latencies.Count / wall.Elapsed.TotalSeconds:F0} запросов/с, ошибок {errors}"));
        Console.WriteLine(allOk ? "Бюджет поиска через API выполнен: p95 < 60 мс для всех запросов." : "Бюджет поиска через API НЕ выполнен для части запросов.");
        await host.StopAsync();
        DiskApp.Infrastructure.Sqlite.SqliteConnectionFactory.ClearAllPools();
        return allOk && errors == 0 ? 0 : 1;
    }

    private static async Task<Contracts.Search.SearchResponse> SearchAsync(HttpClient client, string query)
    {
        var uri = ApiRoutes.V1.Search + "?limit=50&q=" + Uri.EscapeDataString(query);
        var response = await client.GetFromJsonAsync(new Uri(uri, UriKind.Relative), DiskAppJsonContext.Default.SearchResponse);
        return response ?? throw new HttpRequestException("Пустой ответ.");
    }

    private static double Percentile(List<double> sorted, double fraction)
        => sorted.Count == 0 ? double.NaN : sorted[Math.Clamp((int)Math.Ceiling(fraction * sorted.Count) - 1, 0, sorted.Count - 1)];
}

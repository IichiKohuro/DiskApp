using System.Diagnostics;
using System.Globalization;

using DiskApp.Core.Abstractions;
using DiskApp.Core.Domain;
using DiskApp.Core.Events;
using DiskApp.Core.Indexing;
using DiskApp.Core.Paths;
using DiskApp.Infrastructure.FileSystem;
using DiskApp.Infrastructure.Sqlite;
using DiskApp.Infrastructure.Sqlite.Repositories;

namespace DiskApp.Benchmarks.Indexing;

/// <summary>
/// Проверка бюджетов этапа 2 (PLAN.md, 2.6): индекс 100 тыс. файлов строится не дольше 60 с, пик памяти сканера
/// не больше 100 МБ, повторные сканирования не наращивают дескрипторы и память. Это не BenchmarkDotNet: одно
/// дерево создаётся один раз (создание 100 тыс. файлов само по себе долгое), затем выполняются и замеряются
/// полное сканирование, сверка без изменений и повторное полное сканирование.
/// Запуск: <c>dotnet run -c Release --project tests/DiskApp.Benchmarks -- scan-budget [число файлов]</c>.
/// </summary>
internal static class ScanBudgetCheck
{
    private static readonly string[] Extensions = ["txt", "pdf", "jpg", "docx", "cs", "zip", "mp4", "xlsx", "png", "md"];

    public static async Task<int> RunAsync(int files)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "DiskApp.Benchmarks", "scan-" + Guid.NewGuid().ToString("N"));
        var filesRoot = Path.Combine(workspace, "files");
        Directory.CreateDirectory(filesRoot);

        try
        {
            Console.WriteLine($"Создание дерева из {files:N0} файлов в {filesRoot}…");
            var generation = Stopwatch.StartNew();
            var directories = GenerateTree(filesRoot, files);
            Console.WriteLine($"Создано за {generation.Elapsed.TotalSeconds:F1} с: {files:N0} файлов, {directories:N0} папок");

            await using var database = new SqliteDatabase(Path.Combine(workspace, "index.db"));
            await database.InitializeAsync();
            var clock = SystemClock.Instance;
            var roots = new SqliteRootRepository(database, clock);
            var entries = new SqliteFileIndexRepository(database);
            var scanLog = new SqliteScanLogRepository(database);
            var root = await roots.AddAsync(new Root { Path = filesRoot, Label = "bench" });
            var factory = new EntryFactory(PathNormalizer.Current, FileKindMap.Default);
            var events = new IndexEventBus();
            var drive = new DriveInfoProvider().Describe(filesRoot);
            var pipeline = new ScanPipeline(new DirectoryScanner(FileSystemLister.Instance, factory, ThreadBackgroundPriority.Instance), entries, scanLog, events, clock, factory);
            var reconcile = new ReconcileRunner(FileSystemLister.Instance, entries, scanLog, events, clock, factory);
            var request = new ScanRequest
            {
                Root = root,
                Matcher = ExclusionMatcher.Create(ExclusionRule.Defaults, root, PathNormalizer.Current),
                DegreeOfParallelism = drive.RecommendedParallelism,
            };

            Console.WriteLine($"Диск: {drive.Media}, ключ {drive.DiskKey}, потоков обхода {request.DegreeOfParallelism}");
            using var process = Process.GetCurrentProcess();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            process.Refresh();
            var baselineWorkingSet = process.WorkingSet64;
            var baselineHandles = process.HandleCount;

            var (first, firstPeak) = await MeasureAsync(process, () => pipeline.RunAsync(request, PauseToken.None, null, CancellationToken.None));
            Report("Полное сканирование (первое)", first, firstPeak, baselineWorkingSet);

            var (quiet, quietPeak) = await MeasureAsync(process, () => reconcile.RunAsync(request, PauseToken.None, CancellationToken.None));
            Report("Сверка без изменений", quiet, quietPeak, baselineWorkingSet);

            var (second, secondPeak) = await MeasureAsync(process, () => pipeline.RunAsync(request, PauseToken.None, null, CancellationToken.None));
            Report("Полное сканирование (повторное)", second, secondPeak, baselineWorkingSet);

            // Рост рабочего набора после первых сканирований — кэш страниц SQLite (64 МБ на соединение) и mmap файла индекса;
            // утечка выглядела бы как рост от прогона к прогону. Печатаем память и дескрипторы после каждого.
            for (var i = 0; i < 6; i++)
            {
                await pipeline.RunAsync(request, PauseToken.None, null, CancellationToken.None);
                await reconcile.RunAsync(request, PauseToken.None, CancellationToken.None);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                process.Refresh();
                Console.WriteLine($"Прогон {i + 1}: дескрипторов {process.HandleCount} (было {baselineHandles}), рабочий набор {Mb(process.WorkingSet64)} МБ, управляемая куча {Mb(GC.GetTotalMemory(forceFullCollection: false))} МБ");
            }

            var budgetOk = first.Elapsed <= TimeSpan.FromSeconds(60) && firstPeak - baselineWorkingSet <= 100L * 1024 * 1024;
            Console.WriteLine(budgetOk ? "Бюджеты этапа 2 выполнены." : "Бюджет этапа 2 НЕ выполнен.");
            return budgetOk ? 0 : 1;
        }
        finally
        {
            DiskApp.Infrastructure.Sqlite.SqliteConnectionFactory.ClearAllPools();
            TryDelete(workspace);
        }
    }

    private static async Task<(ScanOutcome Outcome, long PeakWorkingSet)> MeasureAsync(Process process, Func<Task<ScanOutcome>> run)
    {
        using var sampling = new CancellationTokenSource();
        long peak = 0;
        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                process.Refresh();
                peak = Math.Max(peak, process.WorkingSet64);
                await Task.Delay(25).ConfigureAwait(false);
            }
        });

        var outcome = await run();
        await sampling.CancelAsync();
        await sampler;
        return (outcome, peak);
    }

    private static void Report(string title, ScanOutcome outcome, long peak, long baseline)
        => Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{title}: {outcome.Elapsed.TotalSeconds:F2} с, найдено {outcome.Found:N0}, добавлено {outcome.Written.Inserted:N0}, обновлено {outcome.Written.Updated:N0}, не изменилось {outcome.Written.Unchanged:N0}, удалено {outcome.Removed:N0}, {outcome.Found / Math.Max(0.001, outcome.Elapsed.TotalSeconds):N0} записей/с, пик рабочего набора +{Mb(peak - baseline)} МБ"));

    private static long Mb(long bytes) => bytes / (1024 * 1024);

    private static int GenerateTree(string root, int files)
    {
        // 100 папок верхнего уровня × 10 подпапок × 3 уровня: примерно как рабочий каталог с проектами и документами.
        var leaves = new List<string>();
        for (var top = 0; top < 100; top++)
        {
            for (var sub = 0; sub < 10; sub++)
            {
                var path = Path.Combine(root, $"project{top:000}", $"part{sub:00}", "docs");
                Directory.CreateDirectory(path);
                leaves.Add(path);
            }
        }

        Parallel.For(0, files, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            var folder = leaves[i % leaves.Count];
            File.WriteAllText(Path.Combine(folder, $"file{i:000000}.{Extensions[i % Extensions.Length]}"), "x");
        });

        return leaves.Count * 3 + 100;
    }

    private static void TryDelete(string path)
    {
        for (var attempt = 0; attempt < 5 && Directory.Exists(path); attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
                Thread.Sleep(500);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(500);
            }
        }
    }
}

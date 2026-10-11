using DiskApp.Benchmarks.Indexing;
using DiskApp.Benchmarks.Search;
using DiskApp.Benchmarks.Server;

namespace DiskApp.Benchmarks;

/// <summary>
/// Дымовой прогон бюджетов для CI (PLAN.md, этап 10.1): те же проверки сканирования, поиска и поиска через API на малых
/// объёмах — 10 тыс. файлов и 50 тыс. записей. Пороги бюджетов не применяются: машины CI медленнее и шумнее, поэтому
/// прогон падает только на исключении, а таблицы попадают в сводку сборки. Полный прогон на 1 млн записей —
/// <c>scripts/measure.ps1</c> перед релизом и после оптимизаций.
/// Запуск: <c>dotnet run -c Release --project tests/DiskApp.Benchmarks -- smoke</c>.
/// </summary>
internal static class SmokeCheck
{
    private const int ScanFiles = 10_000;
    private const int SearchRecords = 50_000;

    public static async Task<int> RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DiskApp.Benchmarks");
        var database = Path.Combine(directory, $"smoke-{Guid.NewGuid():N}.db");
        try
        {
            Console.WriteLine($"## Сканирование, {ScanFiles:N0} файлов");
            var scan = await ScanBudgetCheck.RunAsync(ScanFiles);
            Console.WriteLine();
            Console.WriteLine($"## Поиск, {SearchRecords:N0} записей");
            var search = await SearchBudgetCheck.RunAsync(SearchRecords, database);
            Console.WriteLine();
            Console.WriteLine("## Поиск через API");
            var api = await ApiBudgetCheck.RunAsync(database);
            Console.WriteLine();
            Console.WriteLine($"Итог проверок (0 — бюджет выполнен; в дымовом прогоне не обязателен): сканирование {scan}, поиск {search}, API {api}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Сбой дымового прогона: {exception}");
            return 1;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var file in new[] { database, database + "-wal", database + "-shm" })
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
        }
    }
}

using System.Globalization;

using BenchmarkDotNet.Running;

using DiskApp.Benchmarks.Indexing;

namespace DiskApp.Benchmarks;

internal static class Program
{
    // BenchmarkDotNet для микробенчмарков (индекс SQLite); «scan-budget [файлов]» — проверка бюджетов сканирования этапа 2;
    // «search-budget [записей] [база]» — поиск этапа 3; «api-budget [база]» — поиск через API этапа 4.7;
    // «smoke» — все три на малых объёмах без порогов, для CI (этап 10.1).
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "smoke", StringComparison.OrdinalIgnoreCase))
        {
            return await SmokeCheck.RunAsync();
        }

        if (args.Length > 0 && string.Equals(args[0], "scan-budget", StringComparison.OrdinalIgnoreCase))
        {
            var files = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 100_000;
            return await ScanBudgetCheck.RunAsync(files);
        }

        if (args.Length > 0 && string.Equals(args[0], "search-budget", StringComparison.OrdinalIgnoreCase))
        {
            var records = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 1_000_000;
            return await Search.SearchBudgetCheck.RunAsync(records, args.Length > 2 ? args[2] : null);
        }

        if (args.Length > 0 && string.Equals(args[0], "api-budget", StringComparison.OrdinalIgnoreCase))
        {
            return await Server.ApiBudgetCheck.RunAsync(args.Length > 1 ? args[1] : null);
        }

        var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return summaries.Any(static summary => summary.HasCriticalValidationErrors) ? 1 : 0;
    }
}

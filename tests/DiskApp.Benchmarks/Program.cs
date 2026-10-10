using System.Globalization;

using BenchmarkDotNet.Running;

using DiskApp.Benchmarks.Indexing;

namespace DiskApp.Benchmarks;

internal static class Program
{
    // BenchmarkDotNet для микробенчмарков (индекс SQLite); «scan-budget [файлов]» — проверка бюджетов сканирования этапа 2.
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "scan-budget", StringComparison.OrdinalIgnoreCase))
        {
            var files = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 100_000;
            return await ScanBudgetCheck.RunAsync(files);
        }

        var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return summaries.Any(static summary => summary.HasCriticalValidationErrors) ? 1 : 0;
    }
}

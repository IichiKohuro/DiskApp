using BenchmarkDotNet.Running;

namespace DiskApp.Benchmarks;

internal static class Program
{
    // Первые бенчмарки (сканер, конвейер записи, поиск) появятся в этапах 2.6 и 3.6.
    public static int Main(string[] args)
    {
        var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
        return summaries.Any(static summary => summary.HasCriticalValidationErrors) ? 1 : 0;
    }
}

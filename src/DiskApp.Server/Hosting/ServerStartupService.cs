using System.Diagnostics;

using DiskApp.Core.Abstractions;
using DiskApp.Core.Indexing;
using DiskApp.Core.Operations;
using DiskApp.Infrastructure.Sqlite;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DiskApp.Server.Hosting;

/// <summary>
/// Запуск сервера до того, как Kestrel начнёт принимать запросы: открыть базу (проверка целостности, миграции),
/// затем запустить индексатор. Остановка — в обратном порядке: индексатор останавливается раньше, чем закрывается база.
/// </summary>
internal sealed class ServerStartupService(
    IServiceProvider services,
    SqliteDatabase database,
    ServerState state,
    DiskAppServerOptions options,
    IClock clock,
    ILogger<ServerStartupService> logger) : IHostedService
{
    // После пробуждения сетевым и USB-дискам нужно время, чтобы вернуться: проверка корней чуть откладывается.
    private static readonly TimeSpan ResumeSettleDelay = TimeSpan.FromSeconds(10);

    private IndexerService? _indexer;
    private IDisposable? _resumeSubscription;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        state.MarkStarting(clock.UtcNow);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (Path.GetDirectoryName(database.DatabasePath) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }

            var initialization = await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
            state.SetDatabase(initialization);
            ServerLog.DatabaseReady(logger, initialization.Path, stopwatch.ElapsedMilliseconds, initialization.SchemaVersion,
                initialization.AppliedMigrations.Count, initialization.WasCreated, initialization.WasRecovered);
        }
        catch (Exception exception)
        {
            state.FailDatabase(exception);
            throw;
        }

        if (options.StartIndexer)
        {
            stopwatch.Restart();
            _indexer = services.GetRequiredService<IndexerService>();
            await _indexer.StartAsync(cancellationToken).ConfigureAwait(false);
            ServerLog.IndexerStarted(logger, stopwatch.ElapsedMilliseconds);

            if (services.GetService<ISystemResumeSource>() is { } resume)
            {
                var indexer = _indexer;
                _resumeSubscription = resume.Subscribe(() => _ = OnSystemResumedAsync(indexer));
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _resumeSubscription?.Dispose();

        // Операции останавливаются первыми: они сообщают индексатору о затронутых папках. Время шагов — в журнале: по нему
        // ищется, что задерживает выход (FEEDBACK.md, 2026-10-14).
        var stopwatch = Stopwatch.StartNew();
        await services.GetRequiredService<FileOperationService>().DisposeAsync().ConfigureAwait(false);
        var operationsMs = stopwatch.ElapsedMilliseconds;
        if (_indexer is { } indexer)
        {
            stopwatch.Restart();
            await indexer.DisposeAsync().ConfigureAwait(false);
            ServerLog.IndexerStopped(logger, stopwatch.ElapsedMilliseconds, operationsMs);
        }
    }

    private async Task OnSystemResumedAsync(IndexerService indexer)
    {
        try
        {
            ServerLog.SystemResumed(logger);
            await Task.Delay(ResumeSettleDelay).ConfigureAwait(false);
            indexer.OnSystemResumed();
        }
        catch (ObjectDisposedException)
        {
            // Приложение закрывается.
        }
    }
}

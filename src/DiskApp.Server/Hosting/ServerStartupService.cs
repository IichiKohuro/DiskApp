using System.Diagnostics;

using DiskApp.Core.Abstractions;
using DiskApp.Core.Indexing;
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
    private IndexerService? _indexer;

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
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_indexer is { } indexer)
        {
            await indexer.DisposeAsync().ConfigureAwait(false);
            ServerLog.IndexerStopped(logger);
        }
    }
}

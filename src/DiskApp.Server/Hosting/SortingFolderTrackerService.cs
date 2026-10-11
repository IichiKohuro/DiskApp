using DiskApp.Core.Sorting;

using Microsoft.Extensions.Hosting;

namespace DiskApp.Server.Hosting;

/// <summary>
/// Запускает <see cref="SortingFolderTracker"/> вместе с сервером (PLAN.md, этап 17.2): служба подписывается на события
/// при создании, и без этого она появилась бы только при первом обращении.
/// </summary>
internal sealed class SortingFolderTrackerService(SortingFolderTracker tracker) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        GC.KeepAlive(tracker);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

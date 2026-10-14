using DiskApp.Core.History;

using Microsoft.Extensions.Hosting;

namespace DiskApp.Server.Hosting;

/// <summary>
/// Запускает историю файлов (PLAN.md, этап 20) раньше индексатора — чтобы ни одно изменение не прошло мимо — и при
/// остановке дописывает накопленное, пока база ещё открыта.
/// </summary>
internal sealed class FileHistoryHostedService(FileHistoryService history) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        history.Start();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await history.DisposeAsync().ConfigureAwait(false);
}

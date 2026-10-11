using DiskApp.Core.Events;
using DiskApp.Core.Search;

using Microsoft.Extensions.Hosting;

namespace DiskApp.Server.Hosting;

/// <summary>
/// Сбрасывает кэши поиска (корни, уточнение запроса, расширения), когда меняется индекс или состояние корня:
/// иначе несколько секунд после сканирования поиск отдавал бы прежний набор, а записи нового корня — без полного пути.
/// </summary>
internal sealed class SearchCacheInvalidator(IIndexEventBus events, SearchService search) : IHostedService
{
    private IDisposable? _changes;
    private IDisposable? _status;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _changes = events.Subscribe<EntriesChangedEvent>(_ => search.InvalidateCaches());
        _status = events.Subscribe<RootStatusChangedEvent>(_ => search.InvalidateCaches());
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _changes?.Dispose();
        _status?.Dispose();
        return Task.CompletedTask;
    }
}

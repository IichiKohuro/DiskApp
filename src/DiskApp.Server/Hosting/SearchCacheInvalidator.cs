using DiskApp.Core.Abstractions;
using DiskApp.Core.Events;
using DiskApp.Core.Search;
using DiskApp.Core.Settings;

using Microsoft.Extensions.Hosting;

namespace DiskApp.Server.Hosting;

/// <summary>
/// Сбрасывает кэши поиска (корни, уточнение запроса, расширения), когда меняется индекс или состояние корня:
/// иначе несколько секунд после сканирования поиск отдавал бы прежний набор, а записи нового корня — без полного пути.
/// Изменённые настройки поиска применяются без перезапуска (PLAN.md, этап 9.1).
/// </summary>
internal sealed class SearchCacheInvalidator(IIndexEventBus events, SearchService search, ISettingsStore<AppSettings> settings) : IHostedService
{
    private IDisposable? _changes;
    private IDisposable? _status;
    private IDisposable? _settings;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _changes = events.Subscribe<EntriesChangedEvent>(_ => search.InvalidateCaches());
        _status = events.Subscribe<RootStatusChangedEvent>(_ => search.InvalidateCaches());
        _settings = settings.Subscribe(changed => search.UpdateOptions(SearchOptions.FromSettings(changed.Search)));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _changes?.Dispose();
        _status?.Dispose();
        _settings?.Dispose();
        return Task.CompletedTask;
    }
}

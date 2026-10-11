using DiskApp.Client;
using DiskApp.Contracts.Roots;
using DiskApp.UI.Models;

namespace DiskApp.UI.Services;

/// <summary>
/// <see cref="IRootsService"/> поверх клиента API: список корней дополняется ходом идущего сканирования из
/// состояния индексации.
/// </summary>
public sealed class ApiRootsService(IDiskAppApi api) : IRootsService
{
    private readonly IDiskAppApi _api = api ?? throw new ArgumentNullException(nameof(api));

    /// <inheritdoc />
    public async Task<IReadOnlyList<RootInfo>> GetRootsAsync(CancellationToken cancellationToken = default)
    {
        var roots = _api.GetRootsAsync(cancellationToken);
        var status = _api.GetIndexStatusAsync(cancellationToken);
        await Task.WhenAll(roots, status).ConfigureAwait(false);

        var progress = (await status.ConfigureAwait(false)).Roots.ToDictionary(static root => root.RootId);
        return [.. (await roots.ConfigureAwait(false)).Select(root => ToInfo(root, progress.GetValueOrDefault(root.Id)))];
    }

    /// <inheritdoc />
    public async Task<RootSummary> GetSummaryAsync(long rootId, CancellationToken cancellationToken = default)
    {
        var stats = await _api.GetRootStatsAsync(rootId, cancellationToken).ConfigureAwait(false);
        return new RootSummary(stats.FileCount, stats.DirectoryCount, stats.TotalSize);
    }

    /// <inheritdoc />
    public async Task<RootInfo> AddAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var root = await _api.CreateRootAsync(new CreateRootRequest { Path = path }, cancellationToken).ConfigureAwait(false);
        return ToInfo(root, null);
    }

    /// <inheritdoc />
    public Task RescanAsync(long rootId, CancellationToken cancellationToken = default) => _api.RescanRootAsync(rootId, cancellationToken);

    /// <inheritdoc />
    public Task PauseAsync(long rootId, CancellationToken cancellationToken = default) => _api.PauseRootAsync(rootId, cancellationToken);

    /// <inheritdoc />
    public Task ResumeAsync(long rootId, CancellationToken cancellationToken = default) => _api.ResumeRootAsync(rootId, cancellationToken);

    /// <summary>
    /// Корень API в модель интерфейса.
    /// </summary>
    public static RootInfo ToInfo(RootDto root, RootIndexStatusDto? status)
    {
        ArgumentNullException.ThrowIfNull(root);

        return new RootInfo
        {
            Id = root.Id,
            Label = root.Label,
            Path = root.Path,
            State = !root.IsEnabled ? RootState.Disabled : root.Status switch
            {
                RootStatus.Offline => RootState.Offline,
                RootStatus.Scanning => RootState.Scanning,
                RootStatus.Paused => RootState.Paused,
                RootStatus.Error => RootState.Error,
                _ => RootState.Online,
            },
            Message = root.ErrorMessage ?? status?.Message,
            LastScan = root.LastScanUtc,
            Scanned = status?.Progress?.Scanned,
        };
    }
}

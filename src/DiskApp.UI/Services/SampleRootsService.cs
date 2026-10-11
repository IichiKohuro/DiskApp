using DiskApp.UI.Models;

namespace DiskApp.UI.Services;

/// <summary>
/// Корни для превью и тестов: четыре корня из <see cref="SampleData"/>, действия меняют только состояние в памяти.
/// </summary>
public sealed class SampleRootsService : IRootsService
{
    private readonly List<RootInfo> _roots;
    private readonly Lock _gate = new();

    /// <summary>
    /// Создаёт корни макета; третий — недоступен, чтобы было видно состояние.
    /// </summary>
    public SampleRootsService()
    {
        var now = DateTimeOffset.Now;
        _roots = [.. SampleData.Roots.Select((path, index) => new RootInfo
        {
            Id = index + 1,
            Label = Text.PathText.FileName(path),
            Path = path,
            State = index == 3 ? RootState.Offline : RootState.Online,
            Message = index == 3 ? "Диск E: не подключён" : null,
            LastScan = now.AddMinutes(-5 * (index + 1)),
        })];
    }

    /// <summary>Добавленные пути в порядке добавления.</summary>
    public List<string> Added { get; } = [];

    /// <summary>Действия: «rescan:1», «pause:2».</summary>
    public List<string> Calls { get; } = [];

    /// <inheritdoc />
    public Task<IReadOnlyList<RootInfo>> GetRootsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<RootInfo>>([.. _roots]);
        }
    }

    /// <inheritdoc />
    public Task<RootSummary> GetSummaryAsync(long rootId, CancellationToken cancellationToken = default)
        => Task.FromResult(new RootSummary(10_000 * rootId, 800 * rootId, 1_500_000_000L * rootId));

    /// <inheritdoc />
    public Task<RootInfo> AddAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_gate)
        {
            var root = new RootInfo { Id = _roots.Count + 1, Label = Text.PathText.FileName(path), Path = path, State = RootState.Scanning };
            _roots.Add(root);
            Added.Add(path);
            return Task.FromResult(root);
        }
    }

    /// <inheritdoc />
    public Task RescanAsync(long rootId, CancellationToken cancellationToken = default) => Record("rescan", rootId, RootState.Scanning);

    /// <inheritdoc />
    public Task PauseAsync(long rootId, CancellationToken cancellationToken = default) => Record("pause", rootId, RootState.Paused);

    /// <inheritdoc />
    public Task ResumeAsync(long rootId, CancellationToken cancellationToken = default) => Record("resume", rootId, RootState.Online);

    /// <inheritdoc />
    public Task RemoveAsync(long rootId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Calls.Add($"remove:{rootId}");
            _roots.RemoveAll(root => root.Id == rootId);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<RootInfo> UpdateAsync(RootInfo root, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        lock (_gate)
        {
            Calls.Add($"update:{root.Id}");
            var index = _roots.FindIndex(candidate => candidate.Id == root.Id);
            _roots[index] = root;
            return Task.FromResult(root);
        }
    }

    private Task Record(string action, long rootId, RootState state)
    {
        lock (_gate)
        {
            Calls.Add($"{action}:{rootId}");
            var index = _roots.FindIndex(root => root.Id == rootId);
            if (index >= 0)
            {
                _roots[index] = _roots[index] with { State = state };
            }
        }

        return Task.CompletedTask;
    }
}

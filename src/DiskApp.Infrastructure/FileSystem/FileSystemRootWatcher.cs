using DiskApp.Core.Domain;
using DiskApp.Core.Indexing.Watching;
using DiskApp.Core.Paths;

namespace DiskApp.Infrastructure.FileSystem;

/// <summary>
/// Наблюдатель за корнем на <see cref="FileSystemWatcher"/> (PLAN.md, этап 2.3): всё поддерево, имена файлов и папок,
/// время, размер и атрибуты, буфер 64 КБ (больше не помогает на сетевых путях и тратит невыгружаемую память).
/// События переводятся в пути от корня; переполнение буфера сообщается отдельно.
/// </summary>
public sealed class FileSystemRootWatcher : IRootWatcher
{
    /// <summary>Размер буфера событий ОС.</summary>
    public const int BufferSize = 64 * 1024;

    private readonly FileSystemWatcher _watcher;
    private readonly PathNormalizer _paths;
    private bool _disposed;

    /// <summary>
    /// Создаёт наблюдателя; события пойдут после <see cref="Start"/>.
    /// </summary>
    public FileSystemRootWatcher(Root root, PathNormalizer paths)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(paths);

        _paths = paths;
        _watcher = new FileSystemWatcher(root.Path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes,
            InternalBufferSize = BufferSize,
        };

        _watcher.Created += (_, e) => Raise(RawChangeKind.Created, e.Name);
        _watcher.Changed += (_, e) => Raise(RawChangeKind.Changed, e.Name);
        _watcher.Deleted += (_, e) => Raise(RawChangeKind.Deleted, e.Name);
        _watcher.Renamed += (_, e) => RaiseRenamed(e.OldName, e.Name);
        _watcher.Error += (_, e) =>
        {
            var exception = e.GetException();
            Failed?.Invoke(this, new WatcherFailure(exception is InternalBufferOverflowException, exception.Message));
        };
    }

    /// <inheritdoc />
    public event EventHandler<RawChange>? Changed;

    /// <inheritdoc />
    public event EventHandler<WatcherFailure>? Failed;

    /// <inheritdoc />
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _watcher.EnableRaisingEvents = true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
    }

    private void Raise(RawChangeKind kind, string? name)
    {
        if (!string.IsNullOrEmpty(name))
        {
            Changed?.Invoke(this, new RawChange(kind, _paths.NormalizeRelative(name)));
        }
    }

    private void RaiseRenamed(string? oldName, string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        Changed?.Invoke(this, string.IsNullOrEmpty(oldName)
            ? new RawChange(RawChangeKind.Created, _paths.NormalizeRelative(name))
            : new RawChange(RawChangeKind.Renamed, _paths.NormalizeRelative(name), _paths.NormalizeRelative(oldName)));
    }
}

/// <summary>
/// Фабрика наблюдателей на <see cref="FileSystemWatcher"/>.
/// </summary>
public sealed class FileSystemRootWatcherFactory(PathNormalizer paths) : IRootWatcherFactory
{
    /// <inheritdoc />
    public IRootWatcher Create(Root root) => new FileSystemRootWatcher(root, paths);
}

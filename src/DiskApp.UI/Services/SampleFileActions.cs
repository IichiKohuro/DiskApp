using DiskApp.UI.Models;
using DiskApp.UI.Text;

namespace DiskApp.UI.Services;

/// <summary>
/// Действия над файлами для превью и тестов: ничего не трогают на диске, только запоминают вызовы.
/// Можно попросить упасть, чтобы проверить показ ошибок.
/// </summary>
public sealed class SampleFileActions : IFileActions
{
    private readonly List<string> _calls = [];
    private readonly Lock _gate = new();

    /// <summary>Вызовы в порядке поступления: «Reveal: путь», «Rename: путь → имя».</summary>
    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>Если задано, каждое действие бросает это исключение.</summary>
    public Exception? FailWith { get; set; }

    /// <inheritdoc />
    public Task OpenWithAsync(FileEntry entry, CancellationToken cancellationToken = default) => RecordAsync("OpenWith", entry);

    /// <inheritdoc />
    public Task RevealAsync(FileEntry entry, CancellationToken cancellationToken = default) => RecordAsync("Reveal", entry);

    /// <inheritdoc />
    public Task ShowPropertiesAsync(FileEntry entry, CancellationToken cancellationToken = default) => RecordAsync("Properties", entry);

    /// <inheritdoc />
    public Task RunAsAdminAsync(FileEntry entry, CancellationToken cancellationToken = default) => RecordAsync("RunAsAdmin", entry);

    /// <inheritdoc />
    public Task CopyFilesAsync(IReadOnlyList<FileEntry> entries, CancellationToken cancellationToken = default) => RecordAsync("CopyFiles", entries);

    /// <inheritdoc />
    public Task CopyPathsAsync(IReadOnlyList<FileEntry> entries, CancellationToken cancellationToken = default) => RecordAsync("CopyPaths", entries);

    /// <inheritdoc />
    public Task MoveToRecycleBinAsync(IReadOnlyList<FileEntry> entries, CancellationToken cancellationToken = default) => RecordAsync("Recycle", entries);

    /// <inheritdoc />
    public async Task<FileEntry> RenameAsync(FileEntry entry, string newName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(newName);

        if (PathText.ValidateFileName(newName.Trim()) is { } error)
        {
            throw new ArgumentException(error, nameof(newName));
        }

        await RecordAsync("Rename", entry, " → " + newName.Trim());
        return entry with { FullPath = PathText.WithFileName(entry.FullPath, newName.Trim()), NameHighlights = null };
    }

    /// <inheritdoc />
    public async Task<FileEntry> SetFavoriteAsync(FileEntry entry, bool favorite, CancellationToken cancellationToken = default)
    {
        await RecordAsync(favorite ? "Pin" : "Unpin", entry);
        return entry with { IsFavorite = favorite };
    }

    private Task RecordAsync(string action, FileEntry entry, string suffix = "")
    {
        ArgumentNullException.ThrowIfNull(entry);
        return RecordAsync(action + ": " + entry.FullPath + suffix);
    }

    private Task RecordAsync(string action, IReadOnlyList<FileEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return RecordAsync(action + ": " + string.Join("; ", entries.Select(static entry => entry.FullPath)));
    }

    private Task RecordAsync(string call)
    {
        if (FailWith is { } failure)
        {
            return Task.FromException(failure);
        }

        lock (_gate)
        {
            _calls.Add(call);
        }

        return Task.CompletedTask;
    }
}

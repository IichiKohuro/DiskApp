using DiskApp.Core.Abstractions;
using DiskApp.UI.Services;

namespace DiskApp.Desktop.Shell;

/// <summary>
/// Буфер обмена через оболочку ОС (<see cref="IPlatformShell"/>): текст и файлы в формате Проводника (<c>CF_HDROP</c>),
/// так что вставка в Проводнике копирует файлы, а в мессенджере — прикладывает их.
/// </summary>
internal sealed class ShellClipboardService(IPlatformShell? shell = null) : IClipboardService
{
    /// <inheritdoc />
    public async Task SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = await Shell.CopyPathsToClipboardAsync([text], cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            throw new InvalidOperationException(result.Error!.Message);
        }
    }

    /// <inheritdoc />
    public async Task SetFilesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var result = await Shell.CopyFilesToClipboardAsync(paths, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            throw new InvalidOperationException(result.Error!.Message);
        }
    }

    private IPlatformShell Shell => shell ?? throw new NotSupportedException("Буфер обмена недоступен: нет оболочки ОС.");
}

using DiskApp.Client;
using DiskApp.Contracts.Operations;
using DiskApp.UI.Models;
using DiskApp.UI.Text;

namespace DiskApp.UI.Services;

/// <summary>
/// <see cref="IFileActions"/> поверх клиента API (PLAN.md, этап 7.3): действия оболочки — запросами к серверу,
/// корзина и переименование — операциями его очереди с ожиданием результата, буфер обмена — через
/// <see cref="IClipboardService"/> оболочки.
/// </summary>
public sealed class ApiFileActions(IDiskAppApi api, IClipboardService clipboard) : IFileActions
{
    private static readonly TimeSpan[] PollDelays = [TimeSpan.FromMilliseconds(25), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(250)];

    private readonly IDiskAppApi _api = api ?? throw new ArgumentNullException(nameof(api));
    private readonly IClipboardService _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));

    /// <summary>Сколько ждать завершения операции, прежде чем сказать, что она ещё идёт.</summary>
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public Task OpenWithAsync(FileEntry entry, CancellationToken cancellationToken = default)
        => _api.OpenWithAsync(Id(entry), cancellationToken);

    /// <inheritdoc />
    public Task RevealAsync(FileEntry entry, CancellationToken cancellationToken = default)
        => _api.RevealAsync(Id(entry), cancellationToken);

    /// <inheritdoc />
    public Task ShowPropertiesAsync(FileEntry entry, CancellationToken cancellationToken = default)
        => _api.ShowPropertiesAsync(Id(entry), cancellationToken);

    /// <inheritdoc />
    public Task RunAsAdminAsync(FileEntry entry, CancellationToken cancellationToken = default)
        => _api.RunAsAdminAsync(Id(entry), cancellationToken);

    /// <inheritdoc />
    public Task CopyFilesAsync(IReadOnlyList<FileEntry> entries, CancellationToken cancellationToken = default)
        => _clipboard.SetFilesAsync(Paths(entries), cancellationToken);

    /// <inheritdoc />
    public Task CopyPathsAsync(IReadOnlyList<FileEntry> entries, CancellationToken cancellationToken = default)
        => _clipboard.SetTextAsync(string.Join(Environment.NewLine, Paths(entries)), cancellationToken);

    /// <inheritdoc />
    public async Task MoveToRecycleBinAsync(IReadOnlyList<FileEntry> entries, CancellationToken cancellationToken = default)
    {
        var request = new OperationRequest { Kind = OperationKind.Delete, Sources = Paths(entries), Permanent = false };
        var operation = await _api.EnqueueOperationAsync(request, cancellationToken).ConfigureAwait(false);
        await WaitAsync(operation, conflictMessage: "Удаление ждёт решения по конфликту.", cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<FileEntry> RenameAsync(FileEntry entry, string newName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(newName);

        newName = newName.Trim();
        if (PathText.ValidateFileName(newName) is { } error)
        {
            throw new ArgumentException(error, nameof(newName));
        }

        if (string.Equals(newName, entry.Name, StringComparison.Ordinal))
        {
            return entry;
        }

        var request = new OperationRequest
        {
            Kind = OperationKind.Rename,
            Sources = [entry.FullPath],
            NewName = newName,
            Conflicts = ConflictPolicy.Ask,
        };
        var operation = await _api.EnqueueOperationAsync(request, cancellationToken).ConfigureAwait(false);
        await WaitAsync(operation, conflictMessage: $"«{newName}» уже есть в этой папке.", cancellationToken).ConfigureAwait(false);
        return entry with { FullPath = PathText.WithFileName(entry.FullPath, newName), NameHighlights = null };
    }

    /// <inheritdoc />
    public async Task<FileEntry> SetFavoriteAsync(FileEntry entry, bool favorite, CancellationToken cancellationToken = default)
    {
        if (favorite)
        {
            await _api.AddFavoriteAsync(Id(entry), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _api.RemoveFavoriteAsync(Id(entry), cancellationToken).ConfigureAwait(false);
        }

        return entry with { IsFavorite = favorite };
    }

    /// <inheritdoc />
    /// <remarks>
    /// Файлы идут загрузкой через API, а не операцией копирования: источник обычно вне корней («Загрузки», рабочий стол),
    /// а операции сервера принимают только пути внутри корней.
    /// </remarks>
    public async Task<int> CopyIntoAsync(IReadOnlyList<string> files, FileEntry folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(folder);

        var uploads = files
            .Where(File.Exists)
            .Select(static path => new UploadFile(Path.GetFileName(path), () => File.OpenRead(path), new FileInfo(path).Length))
            .ToArray();
        if (uploads.Length == 0)
        {
            return 0;
        }

        var response = await _api.UploadAsync(folder.FullPath, uploads, ConflictPolicy.KeepBoth, progress: null, cancellationToken).ConfigureAwait(false);
        return response.Saved.Count;
    }

    private static long Id(FileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.Id;
    }

    private static string[] Paths(IReadOnlyList<FileEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return [.. entries.Select(static entry => entry.FullPath)];
    }

    // Операции очереди обычно завершаются за десятки миллисекунд: опрос с нарастающей паузой дешевле подписки на хаб.
    private async Task WaitAsync(OperationDto operation, string conflictMessage, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + OperationTimeout;
        for (var attempt = 0; ; attempt++)
        {
            switch (operation.State)
            {
                case OperationState.Completed:
                    return;
                case OperationState.Failed:
                    throw new InvalidOperationException(operation.Message ?? "Операция не выполнена.");
                case OperationState.Cancelled:
                    throw new OperationCanceledException(operation.Message ?? "Операция отменена.");
                case OperationState.WaitingDecision:
                    await _api.CancelOperationAsync(operation.Id, CancellationToken.None).ConfigureAwait(false);
                    throw new InvalidOperationException(conflictMessage);
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Операция ещё выполняется: её ход виден на странице «Операции».");
            }

            await Task.Delay(PollDelays[Math.Min(attempt, PollDelays.Length - 1)], cancellationToken).ConfigureAwait(false);
            operation = await _api.GetOperationAsync(operation.Id, cancellationToken).ConfigureAwait(false);
        }
    }
}

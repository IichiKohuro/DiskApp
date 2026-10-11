using DiskApp.Core.Abstractions;
using DiskApp.Core.Events;
using DiskApp.Core.Operations;
using DiskApp.Core.Paths;
using DiskApp.Core.Repositories;
using DiskApp.Core.Settings;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskApp.Core.Sorting;

/// <summary>
/// Пути накопителей и рабочих папок следуют за папкой (PLAN.md, этап 17.2): переименование, которое увидел наблюдатель
/// (в том числе сделанное в Проводнике), и перенос или переименование через DiskApp меняют путь в настройках — вместе с
/// вложенными папками и основной папкой-накопителем. Папка, перенесённая в Проводнике в другое место, наблюдателю видна
/// как удаление и появление; её интерфейс показывает «не найдена», и путь меняется в карточке рабочей папки.
/// </summary>
public sealed class SortingFolderTracker : IDisposable
{
    private static readonly Action<ILogger, int, Exception?> LogUpdated =
        LoggerMessage.Define<int>(LogLevel.Information, new EventId(1, nameof(LogUpdated)), "Пути папок разбора обновлены после переименования ({Count})");

    private static readonly Action<ILogger, Exception?> LogFailed =
        LoggerMessage.Define(LogLevel.Warning, new EventId(2, nameof(LogFailed)), "Пути папок разбора не обновились после переименования");

    private readonly FileOperationService _operations;
    private readonly ISettingsStore<AppSettings> _settings;
    private readonly IFolderTagRepository? _folderTags;
    private readonly Action? _folderTagsMoved;
    private readonly PathNormalizer _paths;
    private readonly ILogger _logger;
    private readonly IDisposable _renames;
    private readonly IDisposable _completed;
    private readonly Lock _pendingGate = new();
    private Task _pending = Task.CompletedTask;

    /// <summary>
    /// Создаёт службу и подписывается на события индекса и очереди операций.
    /// </summary>
    /// <param name="events">События индекса: переименования.</param>
    /// <param name="operations">Очередь операций: переносы через DiskApp.</param>
    /// <param name="settings">Настройки с папками разбора.</param>
    /// <param name="paths">Правила сравнения путей.</param>
    /// <param name="logger">Журнал.</param>
    /// <param name="folderTags">Авто-теги папок (этап 19); <c>null</c> — пути авто-тегов не отслеживаются.</param>
    /// <param name="folderTagsMoved">Вызывается, когда пути авто-тегов поменялись: сбросить кэш поиска.</param>
    public SortingFolderTracker(
        IIndexEventBus events,
        FileOperationService operations,
        ISettingsStore<AppSettings> settings,
        PathNormalizer paths,
        ILogger<SortingFolderTracker>? logger = null,
        IFolderTagRepository? folderTags = null,
        Action? folderTagsMoved = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? NullLogger<SortingFolderTracker>.Instance;
        _folderTags = folderTags;
        _folderTagsMoved = folderTagsMoved;
        _renames = events.Subscribe<EntryRenamedEvent>(OnRenamed);
        _completed = events.Subscribe<OperationCompletedEvent>(OnOperationCompleted);
    }

    /// <summary>
    /// Пути после переименования <paramref name="oldPath"/> в <paramref name="newPath"/>: сама папка и всё внутри неё.
    /// </summary>
    /// <returns>Новые настройки разбора или те же, если ни один путь не затронут.</returns>
    public static SortingSettings Remap(SortingSettings sorting, string oldPath, string newPath, PathNormalizer paths)
    {
        ArgumentNullException.ThrowIfNull(sorting);
        ArgumentNullException.ThrowIfNull(paths);

        IReadOnlyList<SortingFolder> MapAll(IReadOnlyList<SortingFolder> folders)
            => [.. folders.Select(folder => MapPath(folder.Path, oldPath, newPath, paths) is { } mapped && !ReferenceEquals(mapped, folder.Path) ? folder with { Path = mapped } : folder)];

        var remapped = sorting with
        {
            Inboxes = MapAll(sorting.Inboxes),
            WorkFolders = MapAll(sorting.WorkFolders),
            MainInbox = MapPath(sorting.MainInbox, oldPath, newPath, paths),
        };
        return remapped.Equals(sorting) ? sorting : remapped;
    }

    /// <summary>
    /// Путь <paramref name="path"/> после переименования <paramref name="oldPath"/> в <paramref name="newPath"/>: та же папка
    /// получает новый путь, вложенная — новый путь с прежним хвостом; остальные возвращаются как были (тот же экземпляр).
    /// </summary>
    public static string? MapPath(string? path, string oldPath, string newPath, PathNormalizer paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (path is null
            || !paths.TryNormalize(oldPath, out var from)
            || !paths.TryNormalize(newPath, out var to)
            || paths.Comparer.Equals(from, to)
            || !paths.TryNormalize(path, out var normalized))
        {
            return path;
        }

        if (paths.Comparer.Equals(normalized, from))
        {
            return to;
        }

        return paths.IsNestedIn(normalized, from) && paths.TryGetRelativePath(from, normalized, out var relative)
            ? paths.Combine(to, relative)
            : path;
    }

    /// <summary>Завершается, когда все начатые обновления настроек записаны (тесты).</summary>
    public Task WhenIdleAsync()
    {
        lock (_pendingGate)
        {
            return _pending;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _renames.Dispose();
        _completed.Dispose();
    }

    private void OnRenamed(EntryRenamedEvent renamed)
    {
        if (renamed.IsDirectory)
        {
            Schedule([(renamed.OldPath, renamed.NewPath)]);
        }
    }

    private void OnOperationCompleted(OperationCompletedEvent completed)
    {
        // Перенос и переименование через DiskApp: пары «откуда → куда» есть у операции, и путь меняется сразу, не дожидаясь
        // наблюдателя (который увидит такую же пару и ничего уже не изменит).
        if (_operations.Get(completed.OperationId) is { Moved.Count: > 0 } info)
        {
            Schedule([.. info.Moved.Select(static moved => (moved.From, moved.To))]);
        }
    }

    private void Schedule(IReadOnlyList<(string From, string To)> moves)
    {
        lock (_pendingGate)
        {
            _pending = ApplyAsync(moves, _pending);
        }
    }

    private async Task ApplyAsync(IReadOnlyList<(string From, string To)> moves, Task previous)
    {
        // Обновления идут по одному, в порядке событий: каждое ждёт предыдущее.
        await previous.ConfigureAwait(false);
        try
        {
            var current = _settings.Current;
            var sorting = moves.Aggregate(current.Sorting, (settings, move) => Remap(settings, move.From, move.To, _paths));
            if (!ReferenceEquals(sorting, current.Sorting))
            {
                await _settings.SaveAsync(current with { Sorting = sorting }).ConfigureAwait(false);
                LogUpdated(_logger, moves.Count, null);
            }

            // Авто-теги (этап 19) хранятся по пути папки и переезжают вместе с ней.
            if (_folderTags is not null)
            {
                var changes = new List<(string From, string To)>();
                foreach (var folder in await _folderTags.GetAllAsync().ConfigureAwait(false))
                {
                    var mapped = moves.Aggregate(folder.Path, (path, move) => MapPath(path, move.From, move.To, _paths)!);
                    if (!ReferenceEquals(mapped, folder.Path))
                    {
                        changes.Add((folder.Path, mapped));
                    }
                }

                if (changes.Count > 0)
                {
                    await _folderTags.MovePathsAsync(changes).ConfigureAwait(false);
                    _folderTagsMoved?.Invoke();
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.Data.Common.DbException)
        {
            LogFailed(_logger, exception);
        }
    }
}

using System.Collections.Concurrent;
using System.IO.Abstractions;

using DiskApp.Core.Domain;
using DiskApp.Core.Events;
using DiskApp.Core.Localization;
using DiskApp.Core.Operations;
using DiskApp.Core.Paths;
using DiskApp.Core.Repositories;
using DiskApp.Core.Results;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiskApp.Core.Sorting;

/// <summary>
/// Запрос разбора: что перенести, куда, в какую новую подпапку и с какими тегами.
/// </summary>
/// <param name="Sources">Нормализованные пути файлов и папок из накопителя.</param>
/// <param name="Destination">Нормализованный путь рабочей папки или её подпапки.</param>
/// <param name="NewFolder">Имя новой подпапки внутри <paramref name="Destination"/>; <c>null</c> — переносить прямо в неё.</param>
/// <param name="Tags">Теги, которые получат перенесённые элементы, вдобавок к уже имеющимся.</param>
/// <param name="Names">Новые имена по порядку <paramref name="Sources"/>; <c>null</c> — прежние имена.</param>
/// <param name="Origin">Кто переносит: пользователь или авторазбор (этап 17.5).</param>
public sealed record SortRequest(IReadOnlyList<string> Sources, string Destination, string? NewFolder, IReadOnlyList<string> Tags, IReadOnlyList<string?>? Names = null, OperationOrigin Origin = OperationOrigin.User);

/// <summary>
/// Бросок файлов в папку-накопитель из окна Drag&amp;Drop (PLAN.md, этап 18.2).
/// </summary>
/// <param name="Sources">Нормализованные пути брошенных файлов и папок; могут лежать вне корней.</param>
/// <param name="Destination">Нормализованный путь папки-накопителя.</param>
/// <param name="Copy">Копировать; иначе — перенести.</param>
public sealed record DropRequest(IReadOnlyList<string> Sources, string Destination, bool Copy);

/// <summary>
/// Разбор входящих (PLAN.md, этап 17): перенос из накопителя в рабочую папку одной операцией очереди с конфликтами
/// по правилу «оставить оба», при необходимости — в новую подпапку. Когда перенос завершился и индекс увидел файлы
/// на новом месте, они получают прежние теги и новые. Отмена — обычный возврат результата операции.
/// </summary>
public sealed class SortingService : IDisposable
{
    /// <summary>Наибольшее число элементов в одном переносе.</summary>
    public const int MaxSources = 1_000;

    /// <summary>Наибольшее число тегов в одном переносе.</summary>
    public const int MaxTags = 20;

    /// <summary>Наибольшая длина имени тега.</summary>
    public const int MaxTagLength = 64;

    /// <summary>Код ошибки: неверный запрос разбора.</summary>
    public const string InvalidCode = "sorting.invalid";

    private readonly FileOperationService _operations;
    private readonly IFileSystem _fileSystem;
    private readonly IRootRepository _roots;
    private readonly IFileIndexRepository _entries;
    private readonly ITagRepository _tags;
    private readonly PathNormalizer _paths;
    private readonly ILogger _logger;
    private readonly IDisposable _subscription;
    private readonly SortingHistory? _history;
    private readonly ConcurrentDictionary<Guid, PendingTags> _pending = new();
    private readonly ConcurrentDictionary<Guid, Task> _tagging = new();

    /// <summary>
    /// Создаёт сервис разбора.
    /// </summary>
    /// <param name="operations">Очередь операций.</param>
    /// <param name="fileSystem">Файловая система.</param>
    /// <param name="events">События индекса и операций.</param>
    /// <param name="roots">Корни.</param>
    /// <param name="entries">Записи индекса.</param>
    /// <param name="tags">Теги.</param>
    /// <param name="paths">Правила путей.</param>
    /// <param name="logger">Журнал.</param>
    /// <param name="history">История решений для подсказок (этап 17.4); <c>null</c> — переносы не запоминаются.</param>
    public SortingService(
        FileOperationService operations,
        IFileSystem fileSystem,
        IIndexEventBus events,
        IRootRepository roots,
        IFileIndexRepository entries,
        ITagRepository tags,
        PathNormalizer paths,
        ILogger<SortingService>? logger = null,
        SortingHistory? history = null)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        ArgumentNullException.ThrowIfNull(events);
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _entries = entries ?? throw new ArgumentNullException(nameof(entries));
        _tags = tags ?? throw new ArgumentNullException(nameof(tags));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? NullLogger<SortingService>.Instance;
        _history = history;
        _subscription = events.Subscribe<OperationCompletedEvent>(OnOperationCompleted);
    }

    /// <summary>Сколько ждать, пока индекс увидит перенесённый файл, прежде чем отказаться от его тегов.</summary>
    public TimeSpan IndexWaitTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Ставит перенос в очередь; теги назначаются после его завершения (<see cref="WhenTaggedAsync"/>). Перенос, который
    /// сделал пользователь, после завершения запоминается в истории решений для подсказок (этап 17.4).
    /// </summary>
    public Task<Result<FileOperationInfo>> MoveAsync(SortRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return MoveCoreAsync(request, fromSorting: true, cancellationToken);
    }

    private async Task<Result<FileOperationInfo>> MoveCoreAsync(SortRequest request, bool fromSorting, CancellationToken cancellationToken)
    {

        if (Validate(request) is { } error)
        {
            return Result.Fail<FileOperationInfo>(error);
        }

        var tags = request.Tags.Select(static tag => tag.Trim()).Where(static tag => tag.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var destination = request.Destination;
        if (request.NewFolder is { } newFolder)
        {
            destination = _paths.Combine(destination, newFolder.Trim());
            try
            {
                _fileSystem.Directory.CreateDirectory(destination);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Result.Fail<FileOperationInfo>(new DomainError(InvalidCode, L.F("Sorting.CannotCreateFolder", destination, exception.Message)));
            }
        }

        // Теги живут в записи индекса, а перенос между корнями создаёт новую запись: прежние теги запоминаются заранее.
        var previous = await GetTagsAsync(request.Sources, cancellationToken).ConfigureAwait(false);

        var names = request.Names?.Select(static name => string.IsNullOrWhiteSpace(name) ? null : name.Trim()).ToArray();
        // Перенос пользователя со страницы «Разбор» — в истории файлов «разобран» (этап 20) и решение для подсказок (17.4).
        var userSort = fromSorting && request.Origin == OperationOrigin.User;
        var origin = userSort ? OperationOrigin.Sorting : request.Origin;
        var result = _operations.Enqueue(new FileOperationRequest(FileOperationKind.Move, request.Sources, destination, Conflicts: ConflictPolicy.KeepBoth, TargetNames: names, Origin: origin));
        var learn = userSort && _history is not null;
        if (result.TryGetValue(out var info) && (tags.Length > 0 || previous.Count > 0 || learn))
        {
            _pending[info.Id] = new PendingTags(tags, previous, learn);

            // Операция могла завершиться раньше, чем запомнились теги.
            if (_operations.Get(info.Id) is { IsActive: false })
            {
                StartTagging(info.Id);
            }
        }

        return result;
    }

    /// <summary>
    /// Бросок файлов в папку-накопитель (PLAN.md, этап 18.2, ADR-0011): перенос или копия одной операцией очереди с
    /// правилом «оставить оба». Перенос идёт как обычный разбор, поэтому прежние теги файлов из корней сохраняются; копия —
    /// новые файлы без тегов. Проверку путей (источники могут лежать вне корней, назначение — только накопитель) делает
    /// вызывающий.
    /// </summary>
    public Task<Result<FileOperationInfo>> DropAsync(DropRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Бросок в накопитель — не решение разбора: в историю подсказок он не попадает.
        if (!request.Copy)
        {
            return MoveCoreAsync(new SortRequest(request.Sources, request.Destination, NewFolder: null, Tags: []), fromSorting: false, cancellationToken);
        }

        if (Validate(new SortRequest(request.Sources, request.Destination, NewFolder: null, Tags: [])) is { } error)
        {
            return Task.FromResult(Result.Fail<FileOperationInfo>(error));
        }

        return Task.FromResult(_operations.Enqueue(new FileOperationRequest(FileOperationKind.Copy, request.Sources, request.Destination, Conflicts: ConflictPolicy.KeepBoth)));
    }

    /// <summary>Ждёт, пока элементам операции назначены теги и перенос записан в историю решений (для тестов и интерфейса).</summary>
    public Task WhenTaggedAsync(Guid operationId) => _tagging.TryGetValue(operationId, out var task) ? task : Task.CompletedTask;

    /// <inheritdoc />
    public void Dispose() => _subscription.Dispose();

    private DomainError? Validate(SortRequest request)
    {
        if (request.Sources.Count == 0)
        {
            return new DomainError(InvalidCode, L.T("Sorting.NothingToMove"));
        }

        if (request.Sources.Count > MaxSources)
        {
            return new DomainError(InvalidCode, L.F("Sorting.TooManyItems", MaxSources));
        }

        if (request.NewFolder is not null && FileOperationService.NameProblem(request.NewFolder.Trim()) is { } problem)
        {
            return new DomainError(InvalidCode, problem);
        }

        if (request.Names is { } names && names.Count != request.Sources.Count)
        {
            return new DomainError(InvalidCode, L.T("FileOperation.TargetNamesMismatch"));
        }

        if (request.Names?.FirstOrDefault(static name => !string.IsNullOrWhiteSpace(name) && FileOperationService.NameProblem(name.Trim()) is not null) is { } bad)
        {
            return new DomainError(InvalidCode, $"«{bad}»: {FileOperationService.NameProblem(bad.Trim())}");
        }

        if (request.Tags.Count > MaxTags)
        {
            return new DomainError(InvalidCode, L.F("Sorting.TooManyTags", MaxTags));
        }

        if (request.Tags.Any(static tag => tag.Trim().Length > MaxTagLength))
        {
            return new DomainError(InvalidCode, L.F("Sorting.TagTooLong", MaxTagLength));
        }

        // Перенос папки в саму себя или в свою подпапку.
        return request.Sources.Any(source => _paths.Comparer.Equals(source, request.Destination) || _paths.IsNestedIn(request.Destination, source))
            ? new DomainError(InvalidCode, L.T("Sorting.IntoItself"))
            : null;
    }

    private async Task<Dictionary<string, IReadOnlyList<string>>> GetTagsAsync(IReadOnlyList<string> sources, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(_paths.Comparer);
        var roots = await _roots.GetAllAsync(cancellationToken).ConfigureAwait(false);
        foreach (var source in sources)
        {
            if (await FindEntryAsync(roots, source, cancellationToken).ConfigureAwait(false) is not { } entry)
            {
                continue;
            }

            var tags = await _tags.GetTagsOfEntryAsync(entry.Id, cancellationToken).ConfigureAwait(false);
            if (tags.Count > 0)
            {
                result[source] = [.. tags.Select(static tag => tag.Name)];
            }
        }

        return result;
    }

    private async Task<FileEntry?> FindEntryAsync(IReadOnlyList<Root> roots, string path, CancellationToken cancellationToken)
    {
        var root = roots
            .Where(candidate => _paths.IsNestedIn(path, candidate.Path))
            .MaxBy(static candidate => candidate.Path.Length);
        return root is not null && _paths.TryGetRelativePath(root.Path, path, out var relative)
            ? await _entries.GetByPathAsync(root.Id, relative, cancellationToken).ConfigureAwait(false)
            : null;
    }

    private void OnOperationCompleted(OperationCompletedEvent completed)
    {
        if (_pending.ContainsKey(completed.OperationId))
        {
            StartTagging(completed.OperationId);
        }
    }

    private void StartTagging(Guid operationId)
    {
        if (_pending.TryRemove(operationId, out var pending))
        {
            _tagging[operationId] = Task.Run(() => ApplyTagsAsync(operationId, pending));
        }
    }

    private async Task ApplyTagsAsync(Guid operationId, PendingTags pending)
    {
        try
        {
            if (_operations.Get(operationId) is not { } info)
            {
                return;
            }

            if (pending.Learn && info.Moved.Count > 0)
            {
                try
                {
                    await _history!.RecordMovesAsync(info.Moved).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Без записи в историю подсказки просто не учтут этот перенос; теги назначаются всё равно.
                    SortingLog.LearningFailed(_logger, exception, operationId);
                }
            }

            var roots = await _roots.GetAllAsync().ConfigureAwait(false);
            foreach (var moved in info.Moved)
            {
                var names = pending.Previous.TryGetValue(moved.From, out var previous) ? previous.Concat(pending.Tags) : pending.Tags;
                var distinct = names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (distinct.Length == 0)
                {
                    continue;
                }

                if (await WaitForEntryAsync(roots, moved.To).ConfigureAwait(false) is not { } entry)
                {
                    SortingLog.EntryNotIndexed(_logger, moved.To);
                    continue;
                }

                foreach (var name in distinct)
                {
                    var tag = await _tags.AddAsync(name).ConfigureAwait(false);
                    await _tags.AssignAsync(entry.Id, tag.Id).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SortingLog.TaggingFailed(_logger, exception, operationId);
        }
    }

    // Индексатор узнаёт о переносе от очереди операций и сверяет папку назначения; запись появляется чуть позже.
    private async Task<FileEntry?> WaitForEntryAsync(IReadOnlyList<Root> roots, string path)
    {
        var deadline = DateTime.UtcNow + IndexWaitTimeout;
        while (true)
        {
            if (await FindEntryAsync(roots, path, CancellationToken.None).ConfigureAwait(false) is { } entry)
            {
                return entry;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return null;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    private sealed record PendingTags(IReadOnlyList<string> Tags, IReadOnlyDictionary<string, IReadOnlyList<string>> Previous, bool Learn);
}

/// <summary>
/// Журнал разбора.
/// </summary>
internal static partial class SortingLog
{
    [LoggerMessage(EventId = 7001, Level = LogLevel.Warning, Message = "Перенесённый элемент {Path} не появился в индексе: теги не назначены")]
    public static partial void EntryNotIndexed(ILogger logger, string path);

    [LoggerMessage(EventId = 7002, Level = LogLevel.Warning, Message = "Теги после переноса {OperationId} не назначены")]
    public static partial void TaggingFailed(ILogger logger, Exception exception, Guid operationId);

    [LoggerMessage(EventId = 7003, Level = LogLevel.Warning, Message = "Перенос {OperationId} не записан в историю подсказок")]
    public static partial void LearningFailed(ILogger logger, Exception exception, Guid operationId);
}

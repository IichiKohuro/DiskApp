using DiskApp.Client;
using DiskApp.Contracts.History;
using DiskApp.UI.Localization;
using DiskApp.UI.Text;

namespace DiskApp.UI.Services;

/// <summary>Событие истории файлов (PLAN.md, этап 20): что, когда и кто сделал с файлом.</summary>
/// <param name="Id">Номер; растёт со временем.</param>
/// <param name="Time">Когда, местное время.</param>
/// <param name="Kind">Что случилось.</param>
/// <param name="Source">Кто сделал.</param>
/// <param name="Path">Где файл после события; у удалённого — где был.</param>
/// <param name="OldPath">Где был до переименования или переноса.</param>
/// <param name="IsDirectory">Папка.</param>
/// <param name="Size">Размер файла; у сводки индекса — число записей.</param>
/// <param name="Details">Теги «+тег, −тег», путь в корзине или причина сводки.</param>
/// <param name="CanRevert">Можно «Вернуть на место» или «Восстановить».</param>
public sealed record FileHistoryEvent(
    long Id,
    DateTimeOffset Time,
    FileEventKind Kind,
    FileEventSource Source,
    string Path,
    string? OldPath,
    bool IsDirectory,
    long Size,
    string? Details,
    bool CanRevert)
{
    /// <summary>Имя после события.</summary>
    public string Name => PathText.FileName(Path);

    /// <summary>Имя до события.</summary>
    public string? OldName => OldPath is null ? null : PathText.FileName(OldPath);
}

/// <summary>Страница ленты истории.</summary>
/// <param name="Items">События, новые сверху.</param>
/// <param name="NextBefore">Номер для следующей страницы; <c>null</c> — дальше ничего.</param>
public sealed record FileHistoryPage(IReadOnlyList<FileHistoryEvent> Items, long? NextBefore);

/// <summary>
/// История файлов для интерфейса (PLAN.md, этап 20.4): лента с отбором, история одного файла, «Вернуть на место» и
/// «Восстановить», сигнал о новых записях (приходит на фоновом потоке).
/// </summary>
public interface IFileHistoryService
{
    /// <summary>В историю записаны новые события.</summary>
    event EventHandler? Changed;

    /// <summary>Лента по отбору, новые сверху.</summary>
    Task<FileHistoryPage> GetAsync(FileHistoryFilter filter, CancellationToken cancellationToken = default);

    /// <summary>История файла или папки через переносы и переименования, новые сверху.</summary>
    Task<IReadOnlyList<FileHistoryEvent>> GetOfPathAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>«Вернуть на место» или «Восстановить».</summary>
    /// <exception cref="InvalidOperationException">Вернуть нельзя; текст объясняет почему.</exception>
    Task RevertAsync(long id, CancellationToken cancellationToken = default);
}

/// <summary>История файлов на сервере: <c>/api/v1/files/history</c> и событие хаба <c>OnFileHistoryChanged</c>.</summary>
public sealed class ApiFileHistoryService : IFileHistoryService
{
    private readonly IDiskAppApi _api;

    /// <summary>Создаёт сервис и подписывается на событие хаба.</summary>
    public ApiFileHistoryService(IDiskAppApi api, DiskAppHubConnection hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        _api = api ?? throw new ArgumentNullException(nameof(api));
        hub.FileHistoryChanged += _ => Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public async Task<FileHistoryPage> GetAsync(FileHistoryFilter filter, CancellationToken cancellationToken = default)
    {
        var page = await _api.GetFileHistoryAsync(filter, cancellationToken).ConfigureAwait(false);
        return new FileHistoryPage([.. page.Items.Select(ToEvent)], page.NextBefore);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FileHistoryEvent>> GetOfPathAsync(string path, CancellationToken cancellationToken = default)
        => [.. (await _api.GetFileHistoryOfPathAsync(path, cancellationToken).ConfigureAwait(false)).Select(ToEvent)];

    /// <inheritdoc />
    public async Task RevertAsync(long id, CancellationToken cancellationToken = default)
    {
        try
        {
            await _api.RevertFileEventAsync(id, cancellationToken).ConfigureAwait(false);
        }
        catch (DiskAppApiException exception)
        {
            throw new InvalidOperationException(exception.Detail ?? exception.Message, exception);
        }
    }

    /// <summary>Событие API в событие интерфейса.</summary>
    public static FileHistoryEvent ToEvent(FileEventDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return new(dto.Id, dto.TimeUtc.ToLocalTime(), dto.Kind, dto.Source, dto.Path, dto.OldPath, dto.IsDirectory, dto.Size, dto.Details, dto.CanRevert);
    }
}

/// <summary>
/// История в памяти: превью дизайнера и тесты. «Вернуть» возвращает путь события назад и пишет новое событие.
/// </summary>
public sealed class SampleFileHistoryService : IFileHistoryService
{
    private readonly Lock _gate = new();
    private readonly List<FileHistoryEvent> _events = [];
    private long _nextId = 1;

    /// <summary>Создаёт пустую историю.</summary>
    public SampleFileHistoryService()
    {
    }

    /// <summary>Создаёт историю с событиями.</summary>
    public SampleFileHistoryService(IEnumerable<FileHistoryEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        foreach (var item in events)
        {
            Add(item);
        }
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <summary>Отборы, с которыми просили ленту.</summary>
    public List<FileHistoryFilter> Requests { get; } = [];

    /// <summary>Номера событий, которые возвращали.</summary>
    public List<long> Reverted { get; } = [];

    /// <summary>Ошибка следующего «Вернуть».</summary>
    public string? FailRevertWith { get; set; }

    /// <summary>Пример ленты для превью: перенос «Разбором», переименование и удаление в Проводнике.</summary>
    public static SampleFileHistoryService WithSamples(DateTimeOffset now)
        => new(
        [
            new FileHistoryEvent(0, now.AddDays(-1).AddHours(-2), FileEventKind.Created, FileEventSource.Explorer, @"C:\Users\Аня\Downloads\акт сверки.pdf", null, false, 182_000, null, false),
            new FileHistoryEvent(0, now.AddDays(-1), FileEventKind.Moved, FileEventSource.Sorting, @"D:\Работа\417\Акты\акт сверки.pdf", @"C:\Users\Аня\Downloads\акт сверки.pdf", false, 182_000, null, true),
            new FileHistoryEvent(0, now.AddHours(-3), FileEventKind.Renamed, FileEventSource.Explorer, @"D:\Работа\417\Смета v2.xlsx", @"D:\Работа\417\Смета.xlsx", false, 48_000, null, true),
            new FileHistoryEvent(0, now.AddHours(-1), FileEventKind.TagsChanged, FileEventSource.DiskApp, @"D:\Работа\417\Смета v2.xlsx", null, false, 48_000, "+417, −черновик", false),
            new FileHistoryEvent(0, now.AddMinutes(-20), FileEventKind.Recycled, FileEventSource.Explorer, @"D:\Работа\417\старый план.docx", null, false, 31_000, null, true),
        ]);

    /// <summary>Добавляет событие с новым номером и сообщает об изменении.</summary>
    public FileHistoryEvent Add(FileHistoryEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        FileHistoryEvent added;
        lock (_gate)
        {
            added = item with { Id = _nextId++ };
            _events.Add(added);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return added;
    }

    /// <inheritdoc />
    public Task<FileHistoryPage> GetAsync(FileHistoryFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        lock (_gate)
        {
            Requests.Add(filter);
            var items = _events
                .Where(item => filter.Before is null || item.Id < filter.Before)
                .Where(item => filter.Kinds.Count == 0 || filter.Kinds.Contains(item.Kind))
                .Where(item => filter.Sources.Count == 0 || filter.Sources.Contains(item.Source))
                .Where(item => filter.Folder is null || PathText.IsSameOrInside(item.Path, filter.Folder) || (item.OldPath is { } old && PathText.IsSameOrInside(old, filter.Folder)))
                .Where(item => filter.Text is null || item.Name.Contains(filter.Text, StringComparison.OrdinalIgnoreCase) || (item.OldName?.Contains(filter.Text, StringComparison.OrdinalIgnoreCase) ?? false))
                .OrderByDescending(static item => item.Id)
                .Take(filter.Limit)
                .ToList();
            return Task.FromResult(new FileHistoryPage(items, items.Count == filter.Limit ? items[^1].Id : null));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FileHistoryEvent>> GetOfPathAsync(string path, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path };
            foreach (var item in _events.OrderByDescending(static item => item.Id))
            {
                if (paths.Contains(item.Path) && item.OldPath is { } old)
                {
                    paths.Add(old);
                }
            }

            IReadOnlyList<FileHistoryEvent> result = [.. _events.Where(item => paths.Contains(item.Path)).OrderByDescending(static item => item.Id)];
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc />
    public Task RevertAsync(long id, CancellationToken cancellationToken = default)
    {
        if (FailRevertWith is { } message)
        {
            FailRevertWith = null;
            throw new InvalidOperationException(message);
        }

        FileHistoryEvent? item;
        lock (_gate)
        {
            item = _events.FirstOrDefault(candidate => candidate.Id == id);
            Reverted.Add(id);
        }

        if (item is null || !item.CanRevert)
        {
            throw new InvalidOperationException(L.T("FileHistory.CannotRevert"));
        }

        Add(item.Kind == FileEventKind.Recycled
            ? item with { Kind = FileEventKind.Restored, Source = FileEventSource.DiskApp, Time = DateTimeOffset.Now, CanRevert = false }
            : item with { Path = item.OldPath!, OldPath = item.Path, Source = FileEventSource.DiskApp, Time = DateTimeOffset.Now });
        return Task.CompletedTask;
    }
}

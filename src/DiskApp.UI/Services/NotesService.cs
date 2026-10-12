using DiskApp.Client;
using DiskApp.Contracts.Library;
using DiskApp.Contracts.Notes;
using DiskApp.UI.Localization;
using DiskApp.UI.Models;

namespace DiskApp.UI.Services;

/// <summary>Заметка (PLAN.md, этап 23).</summary>
/// <param name="Id">Идентификатор.</param>
/// <param name="Title">Заголовок — первая строка текста.</param>
/// <param name="Body">Текст в Markdown.</param>
/// <param name="IsPinned">Закреплена.</param>
/// <param name="Modified">Изменена.</param>
/// <param name="Deleted">Удалена в корзину; <c>null</c> — нет.</param>
/// <param name="Tags">Теги заметки — свой список, не теги файлов.</param>
public sealed record NoteInfo(long Id, string Title, string Body, bool IsPinned, DateTimeOffset Modified, DateTimeOffset? Deleted, IReadOnlyList<TagInfo> Tags);

/// <summary>
/// Заметки и теги заметок (PLAN.md, этап 23, ADR-0014).
/// </summary>
public interface INotesService
{
    /// <summary>Заметки: закреплённые сверху; поиск по тексту, тег, корзина.</summary>
    Task<IReadOnlyList<NoteInfo>> GetNotesAsync(string? query = null, long? tagId = null, bool deleted = false, CancellationToken cancellationToken = default);

    /// <summary>Новая заметка.</summary>
    Task<NoteInfo> CreateAsync(string body, IReadOnlyList<string> tags, CancellationToken cancellationToken = default);

    /// <summary>Сохраняет текст, закрепление и теги; новые имена тегов становятся тегами заметок.</summary>
    Task<NoteInfo> UpdateAsync(long id, string body, bool isPinned, IReadOnlyList<string> tags, CancellationToken cancellationToken = default);

    /// <summary>В корзину заметок; <paramref name="permanent"/> — навсегда, только из корзины.</summary>
    Task DeleteAsync(long id, bool permanent = false, CancellationToken cancellationToken = default);

    /// <summary>Вернуть из корзины.</summary>
    Task<NoteInfo> RestoreAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Теги заметок.</summary>
    Task<IReadOnlyList<TagInfo>> GetTagsAsync(CancellationToken cancellationToken = default);

    /// <summary>Создать тег заметок с цветом.</summary>
    Task<TagInfo> CreateTagAsync(string name, string? color, CancellationToken cancellationToken = default);

    /// <summary>Переименовать или перекрасить тег заметок.</summary>
    Task<TagInfo> UpdateTagAsync(long id, string name, string? color, CancellationToken cancellationToken = default);

    /// <summary>Удалить тег у всех заметок.</summary>
    Task DeleteTagAsync(long id, CancellationToken cancellationToken = default);
}

/// <summary>Заметки на сервере: <c>/api/v1/notes</c> и <c>/api/v1/note-tags</c>.</summary>
public sealed class ApiNotesService(IDiskAppApi api) : INotesService
{
    private readonly IDiskAppApi _api = api ?? throw new ArgumentNullException(nameof(api));

    /// <inheritdoc />
    public async Task<IReadOnlyList<NoteInfo>> GetNotesAsync(string? query = null, long? tagId = null, bool deleted = false, CancellationToken cancellationToken = default)
        => [.. (await _api.GetNotesAsync(query, tagId, deleted, cancellationToken).ConfigureAwait(false)).Select(ToNote)];

    /// <inheritdoc />
    public async Task<NoteInfo> CreateAsync(string body, IReadOnlyList<string> tags, CancellationToken cancellationToken = default)
        => ToNote(await _api.CreateNoteAsync(new NoteRequest { Body = body, Tags = tags }, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public async Task<NoteInfo> UpdateAsync(long id, string body, bool isPinned, IReadOnlyList<string> tags, CancellationToken cancellationToken = default)
        => ToNote(await _api.UpdateNoteAsync(id, new NoteRequest { Body = body, IsPinned = isPinned, Tags = tags }, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public Task DeleteAsync(long id, bool permanent = false, CancellationToken cancellationToken = default) => _api.DeleteNoteAsync(id, permanent, cancellationToken);

    /// <inheritdoc />
    public async Task<NoteInfo> RestoreAsync(long id, CancellationToken cancellationToken = default)
        => ToNote(await _api.RestoreNoteAsync(id, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public async Task<IReadOnlyList<TagInfo>> GetTagsAsync(CancellationToken cancellationToken = default)
        => [.. (await _api.GetNoteTagsAsync(cancellationToken).ConfigureAwait(false)).Select(ToTag)];

    /// <inheritdoc />
    public async Task<TagInfo> CreateTagAsync(string name, string? color, CancellationToken cancellationToken = default)
        => ToTag(await _api.CreateNoteTagAsync(new TagRequest { Name = name.Trim(), Color = color }, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public async Task<TagInfo> UpdateTagAsync(long id, string name, string? color, CancellationToken cancellationToken = default)
        => ToTag(await _api.UpdateNoteTagAsync(id, new TagRequest { Name = name.Trim(), Color = color }, cancellationToken).ConfigureAwait(false));

    /// <inheritdoc />
    public Task DeleteTagAsync(long id, CancellationToken cancellationToken = default) => _api.DeleteNoteTagAsync(id, cancellationToken);

    private static TagInfo ToTag(TagDto dto) => new(dto.Id, dto.Name, dto.Color);

    private static NoteInfo ToNote(NoteDto dto)
        => new(dto.Id, dto.Title, dto.Body, dto.IsPinned, dto.ModifiedUtc.ToLocalTime(), dto.DeletedUtc?.ToLocalTime(), [.. dto.Tags.Select(ToTag)]);
}

/// <summary>
/// Заметки в памяти: превью дизайнера и тесты страницы. Поведение — как у сервера: закреплённые сверху, корзина,
/// теги по именам создаются.
/// </summary>
public sealed class SampleNotesService : INotesService
{
    private readonly Lock _gate = new();
    private readonly List<NoteInfo> _notes = [];
    private readonly List<TagInfo> _tags = [];
    private readonly Func<DateTimeOffset> _clock;
    private long _nextId = 1;
    private long _nextTagId = 1_000;

    /// <summary>Создаёт сервис с примером заметки.</summary>
    public SampleNotesService()
        : this(static () => DateTimeOffset.Now, withSample: true)
    {
    }

    /// <summary>Создаёт сервис.</summary>
    /// <param name="clock">Часы.</param>
    /// <param name="withSample">Положить пример заметки.</param>
    public SampleNotesService(Func<DateTimeOffset> clock, bool withSample = false)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        if (withSample)
        {
            _ = CreateAsync(L.T("Notes.SampleBody"), [L.T("Notes.SampleTag")]);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<NoteInfo>> GetNotesAsync(string? query = null, long? tagId = null, bool deleted = false, CancellationToken cancellationToken = default)
    {
        var terms = (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lock (_gate)
        {
            IReadOnlyList<NoteInfo> result = [.. _notes
                .Where(note => (note.Deleted is not null) == deleted)
                .Where(note => tagId is null || note.Tags.Any(tag => tag.Id == tagId))
                .Where(note => terms.All(term => Fold(note.Body).Contains(Fold(term), StringComparison.Ordinal)))
                .OrderByDescending(static note => note.IsPinned)
                .ThenByDescending(static note => note.Modified)];
            return Task.FromResult(result);
        }
    }

    /// <inheritdoc />
    public Task<NoteInfo> CreateAsync(string body, IReadOnlyList<string> tags, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var note = new NoteInfo(_nextId++, Title(body), body, false, _clock(), null, Resolve(tags));
            _notes.Add(note);
            return Task.FromResult(note);
        }
    }

    /// <inheritdoc />
    public Task<NoteInfo> UpdateAsync(long id, string body, bool isPinned, IReadOnlyList<string> tags, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var index = _notes.FindIndex(note => note.Id == id && note.Deleted is null);
            if (index < 0)
            {
                throw new InvalidOperationException(L.F("Notes.NotFoundSample", id));
            }

            var note = _notes[index] with { Title = Title(body), Body = body, IsPinned = isPinned, Modified = _clock(), Tags = Resolve(tags) };
            _notes[index] = note;
            return Task.FromResult(note);
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(long id, bool permanent = false, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var index = _notes.FindIndex(note => note.Id == id);
            if (index >= 0)
            {
                if (permanent)
                {
                    _notes.RemoveAt(index);
                }
                else
                {
                    _notes[index] = _notes[index] with { Deleted = _clock() };
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<NoteInfo> RestoreAsync(long id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var index = _notes.FindIndex(note => note.Id == id);
            _notes[index] = _notes[index] with { Deleted = null };
            return Task.FromResult(_notes[index]);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TagInfo>> GetTagsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<TagInfo>>([.. _tags.OrderBy(static tag => tag.Name, StringComparer.CurrentCultureIgnoreCase)]);
        }
    }

    /// <inheritdoc />
    public Task<TagInfo> CreateTagAsync(string name, string? color, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var tag = new TagInfo(_nextTagId++, name.Trim(), color);
            _tags.Add(tag);
            return Task.FromResult(tag);
        }
    }

    /// <inheritdoc />
    public Task<TagInfo> UpdateTagAsync(long id, string name, string? color, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var tag = new TagInfo(id, name.Trim(), color);
            _tags[_tags.FindIndex(candidate => candidate.Id == id)] = tag;
            for (var i = 0; i < _notes.Count; i++)
            {
                _notes[i] = _notes[i] with { Tags = [.. _notes[i].Tags.Select(existing => existing.Id == id ? tag : existing)] };
            }

            return Task.FromResult(tag);
        }
    }

    /// <inheritdoc />
    public Task DeleteTagAsync(long id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _tags.RemoveAll(tag => tag.Id == id);
            for (var i = 0; i < _notes.Count; i++)
            {
                _notes[i] = _notes[i] with { Tags = [.. _notes[i].Tags.Where(tag => tag.Id != id)] };
            }
        }

        return Task.CompletedTask;
    }

    private static string Fold(string text) => text.ToLowerInvariant().Replace('ё', 'е');

    private static string Title(string body)
        => body.Split('\n').Select(static line => line.Trim().TrimStart('#').Trim()).FirstOrDefault(static line => line.Length > 0) ?? string.Empty;

    private List<TagInfo> Resolve(IReadOnlyList<string> names)
    {
        var result = new List<TagInfo>();
        foreach (var name in names.Select(static name => name.Trim()).Where(static name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var tag = _tags.Find(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            if (tag is null)
            {
                tag = new TagInfo(_nextTagId++, name, TagPalette.Next(_tags.Select(static existing => existing.Color)));
                _tags.Add(tag);
            }

            result.Add(tag);
        }

        return result;
    }
}

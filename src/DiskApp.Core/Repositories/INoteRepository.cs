using DiskApp.Core.Domain;

namespace DiskApp.Core.Repositories;

/// <summary>
/// Заметки (PLAN.md, этап 23, ADR-0014): список с поиском и фильтром по тегу, правка, закрепление и корзина заметок.
/// </summary>
public interface INoteRepository
{
    /// <summary>Заметки по запросу: закреплённые сверху, затем по времени изменения, новые сначала.</summary>
    /// <param name="query">Что показать.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<IReadOnlyList<Note>> ListAsync(NoteQuery query, CancellationToken cancellationToken = default);

    /// <summary>Заметка, в том числе из корзины; <c>null</c> — нет такой.</summary>
    /// <param name="id">Идентификатор.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<Note?> GetAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Создаёт заметку.</summary>
    /// <param name="body">Текст в Markdown.</param>
    /// <param name="isPinned">Закрепить.</param>
    /// <param name="tagIds">Теги заметок.</param>
    /// <param name="nowUtc">Время создания.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<Note> AddAsync(string body, bool isPinned, IReadOnlyList<long> tagIds, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);

    /// <summary>Заменяет текст, закрепление и теги; <c>null</c> — заметки нет или она в корзине.</summary>
    /// <param name="id">Идентификатор.</param>
    /// <param name="body">Текст в Markdown.</param>
    /// <param name="isPinned">Закрепить.</param>
    /// <param name="tagIds">Теги заметок.</param>
    /// <param name="nowUtc">Время изменения.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<Note?> UpdateAsync(long id, string body, bool isPinned, IReadOnlyList<long> tagIds, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);

    /// <summary>Убирает заметку в корзину; <c>false</c> — нет такой или уже в корзине.</summary>
    /// <param name="id">Идентификатор.</param>
    /// <param name="nowUtc">Время удаления.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<bool> TrashAsync(long id, DateTimeOffset nowUtc, CancellationToken cancellationToken = default);

    /// <summary>Возвращает заметку из корзины; <c>false</c> — её нет в корзине.</summary>
    /// <param name="id">Идентификатор.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<bool> RestoreAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Удаляет заметку из корзины навсегда; <c>false</c> — её нет в корзине.</summary>
    /// <param name="id">Идентификатор.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<bool> PurgeAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Удаляет навсегда заметки, лежащие в корзине с момента раньше <paramref name="cutoffUtc"/>.</summary>
    /// <param name="cutoffUtc">Граница.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<int> PurgeDeletedBeforeAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default);
}

/// <summary>
/// Теги заметок (этап 23): свой список, отдельно от тегов файлов и папок; уникальность имени без учёта регистра.
/// </summary>
public interface INoteTagRepository
{
    /// <summary>Все теги заметок по имени.</summary>
    /// <param name="cancellationToken">Отмена.</param>
    Task<IReadOnlyList<Tag>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Создаёт тег; при совпадении имени возвращает существующий.</summary>
    /// <param name="name">Имя.</param>
    /// <param name="color">Цвет <c>#RRGGBB</c>.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<Tag> AddAsync(string name, string? color = null, CancellationToken cancellationToken = default);

    /// <summary>Переименовывает или перекрашивает; <c>false</c> — тега нет.</summary>
    /// <param name="tag">Тег с новыми именем и цветом.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<bool> UpdateAsync(Tag tag, CancellationToken cancellationToken = default);

    /// <summary>Удаляет тег у всех заметок; <c>false</c> — тега нет.</summary>
    /// <param name="id">Идентификатор.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default);
}

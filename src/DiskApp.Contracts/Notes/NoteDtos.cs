using DiskApp.Contracts.Library;

namespace DiskApp.Contracts.Notes;

/// <summary>Заметка (PLAN.md, этап 23): текст в Markdown, свои теги заметок, закрепление, корзина.</summary>
/// <param name="Id">Идентификатор.</param>
/// <param name="Title">Заголовок — первая строка текста.</param>
/// <param name="Body">Текст в Markdown.</param>
/// <param name="IsPinned">Закреплена: в списке сверху.</param>
/// <param name="CreatedUtc">Создана.</param>
/// <param name="ModifiedUtc">Изменена.</param>
/// <param name="DeletedUtc">Удалена в корзину; <c>null</c> — нет.</param>
/// <param name="Tags">Теги заметки — из списка тегов заметок, не файлов.</param>
public sealed record NoteDto(long Id, string Title, string Body, bool IsPinned, DateTimeOffset CreatedUtc, DateTimeOffset ModifiedUtc, DateTimeOffset? DeletedUtc, IReadOnlyList<TagDto> Tags);

/// <summary>
/// Новая заметка или её правка: <c>POST /api/v1/notes</c>, <c>PUT /api/v1/notes/{id}</c>. Теги — по именам: недостающие
/// создаются в списке тегов заметок.
/// </summary>
public sealed record NoteRequest
{
    /// <summary>Наибольшая длина текста.</summary>
    public const int MaxBodyLength = 1_000_000;

    /// <summary>Наибольшее число тегов у заметки.</summary>
    public const int MaxTags = 20;

    /// <summary>Текст в Markdown.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Закрепить.</summary>
    public bool IsPinned { get; set; }

    /// <summary>Имена тегов заметки.</summary>
    public IReadOnlyList<string> Tags { get; set; } = [];
}

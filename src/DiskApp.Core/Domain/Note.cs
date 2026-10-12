namespace DiskApp.Core.Domain;

/// <summary>
/// Заметка (PLAN.md, этап 23, ADR-0014): текст в Markdown, свои теги заметок, закрепление и корзина заметок.
/// </summary>
public sealed record Note
{
    /// <summary>Идентификатор.</summary>
    public long Id { get; init; }

    /// <summary>Заголовок — первая непустая строка текста без разметки заголовка.</summary>
    public required string Title { get; init; }

    /// <summary>Текст в Markdown.</summary>
    public required string Body { get; init; }

    /// <summary>Закреплена: в списке сверху.</summary>
    public bool IsPinned { get; init; }

    /// <summary>Создана, UTC.</summary>
    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Изменена, UTC.</summary>
    public DateTimeOffset ModifiedUtc { get; init; }

    /// <summary>Удалена в корзину заметок, UTC; <c>null</c> — не удалена.</summary>
    public DateTimeOffset? DeletedUtc { get; init; }

    /// <summary>Теги заметки — не теги файлов: у заметок свой список.</summary>
    public IReadOnlyList<Tag> Tags { get; init; } = [];
}

/// <summary>Что показать в списке заметок.</summary>
/// <param name="Text">Слова, которые должны быть в заголовке или тексте; пусто — все.</param>
/// <param name="TagId">Только с этим тегом заметок; <c>null</c> — с любыми.</param>
/// <param name="Deleted">Корзина заметок вместо обычного списка.</param>
/// <param name="Limit">Сколько заметок вернуть.</param>
public sealed record NoteQuery(string? Text = null, long? TagId = null, bool Deleted = false, int Limit = 500);

/// <summary>Заголовок и выдержка из текста заметки.</summary>
public static class NoteText
{
    /// <summary>Наибольшая длина заголовка.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>Заголовок: первая непустая строка без «#», маркеров списка и флажков; пустая заметка — пусто.</summary>
    public static string TitleOf(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        foreach (var raw in body.AsSpan().EnumerateLines())
        {
            var line = raw.Trim().TrimStart('#').TrimStart();
            if (line.StartsWith("- [ ]") || line.StartsWith("- [x]") || line.StartsWith("- [X]"))
            {
                line = line[5..].TrimStart();
            }
            else if (line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("> "))
            {
                line = line[2..].TrimStart();
            }

            if (!line.IsEmpty)
            {
                var title = line.ToString();
                return title.Length <= MaxTitleLength ? title : title[..MaxTitleLength];
            }
        }

        return string.Empty;
    }
}

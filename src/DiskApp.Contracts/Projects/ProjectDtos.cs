namespace DiskApp.Contracts.Projects;

/// <summary>Проект (PLAN.md, этап 24, ADR-0017): свой номер, название, цвет иконки, основная рабочая папка, заметки.</summary>
/// <param name="Id">Идентификатор.</param>
/// <param name="Number">Номер: «417», «10S-417».</param>
/// <param name="Title">Название; может быть пустым.</param>
/// <param name="Color">Цвет иконки <c>#RRGGBB</c>; <c>null</c> — по умолчанию.</param>
/// <param name="FolderPath">Основная рабочая папка; <c>null</c> — не привязана.</param>
/// <param name="NoteCount">Заметок проекта (без корзины).</param>
/// <param name="CreatedUtc">Создан.</param>
/// <param name="ModifiedUtc">Изменён.</param>
public sealed record ProjectDto(long Id, string Number, string Title, string? Color, string? FolderPath, int NoteCount, DateTimeOffset CreatedUtc, DateTimeOffset ModifiedUtc);

/// <summary>Новый проект или его правка: <c>POST /api/v1/projects</c>, <c>PUT /api/v1/projects/{id}</c>.</summary>
public sealed record ProjectRequest
{
    /// <summary>Номер: 1–20 символов, свой у каждого проекта.</summary>
    public string Number { get; set; } = string.Empty;

    /// <summary>Название, до 200 символов.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Цвет иконки <c>#RRGGBB</c>.</summary>
    public string? Color { get; set; }

    /// <summary>Основная рабочая папка — полный путь; подпапки внутри неё — часть проекта.</summary>
    public string? FolderPath { get; set; }
}

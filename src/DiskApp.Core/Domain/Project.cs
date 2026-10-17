namespace DiskApp.Core.Domain;

/// <summary>
/// Проект (PLAN.md, этап 24, ADR-0017): свой номер — то, по чему пользователь узнаёт работу («417»), название, цвет
/// иконки и одна основная рабочая папка, внутри которой — сколько угодно подпапок. К проекту привязываются заметки.
/// </summary>
public sealed record Project
{
    /// <summary>Наибольшая длина номера.</summary>
    public const int MaxNumberLength = 20;

    /// <summary>Наибольшая длина названия.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>Идентификатор.</summary>
    public long Id { get; init; }

    /// <summary>Номер проекта, свой: «417», «10S-417»; уникален без учёта регистра.</summary>
    public required string Number { get; init; }

    /// <summary>Название; может быть пустым — тогда проект узнают по номеру.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Цвет иконки <c>#RRGGBB</c>; <c>null</c> — по умолчанию.</summary>
    public string? Color { get; init; }

    /// <summary>Основная рабочая папка; <c>null</c> — не привязана.</summary>
    public string? FolderPath { get; init; }

    /// <summary>Сколько заметок проекта (без корзины).</summary>
    public int NoteCount { get; init; }

    /// <summary>Создан, UTC.</summary>
    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Изменён, UTC.</summary>
    public DateTimeOffset ModifiedUtc { get; init; }
}

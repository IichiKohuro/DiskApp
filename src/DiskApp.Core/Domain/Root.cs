namespace DiskApp.Core.Domain;

/// <summary>
/// Корень индекса: папка или диск, который пользователь добавил в DiskApp. Путь хранится нормализованным
/// (<see cref="Paths.PathNormalizer"/>), поэтому сравнивается напрямую через компаратор нормализатора.
/// </summary>
public sealed record Root
{
    /// <summary>Идентификатор в базе; 0 до первого сохранения.</summary>
    public long Id { get; init; }

    /// <summary>Нормализованный абсолютный путь.</summary>
    public required string Path { get; init; }

    /// <summary>Подпись для интерфейса; по умолчанию последний сегмент пути (<see cref="RootValidator"/>).</summary>
    public required string Label { get; init; }

    /// <summary>Участвует ли корень в индексации и поиске.</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>Индексировать скрытые файлы и папки.</summary>
    public bool IncludeHidden { get; init; }

    /// <summary>Индексировать системные файлы и папки.</summary>
    public bool IncludeSystem { get; init; }

    /// <summary>Максимальная глубина вложенности относительно корня; <c>null</c> — без ограничения.</summary>
    public int? MaxDepth { get; init; }

    /// <summary>Когда закончилось последнее полное сканирование; <c>null</c> — ещё не сканировался.</summary>
    public DateTimeOffset? LastScanUtc { get; init; }

    /// <summary>Текущее состояние.</summary>
    public RootStatus Status { get; init; } = RootStatus.Online;

    /// <summary>Сообщение об ошибке при <see cref="RootStatus.Error"/>; иначе <c>null</c>.</summary>
    public string? ErrorMessage { get; init; }
}

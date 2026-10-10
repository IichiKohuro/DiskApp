namespace DiskApp.Core.Domain;

/// <summary>
/// Состояние корня индекса (docs/ARCHITECTURE.md, раздел 5): <c>Online → Scanning → Online</c>, пауза, недоступность, ошибка.
/// </summary>
public enum RootStatus
{
    /// <summary>Доступен, индекс актуален или обновляется наблюдателем.</summary>
    Online,

    /// <summary>Диск извлечён или сеть недоступна; индекс сохранён, периодическая проверка.</summary>
    Offline,

    /// <summary>Идёт полное сканирование или сверка.</summary>
    Scanning,

    /// <summary>Индексация приостановлена пользователем.</summary>
    Paused,

    /// <summary>Ошибка, требующая внимания; текст в <see cref="Root.ErrorMessage"/>.</summary>
    Error,
}

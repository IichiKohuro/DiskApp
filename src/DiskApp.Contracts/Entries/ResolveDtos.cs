namespace DiskApp.Contracts.Entries;

/// <summary>
/// Пути, для которых нужны записи индекса (PLAN.md, этап 8.7): импорт списка путей и перетаскивание файлов в коллекции.
/// </summary>
public sealed record ResolveEntriesRequest
{
    /// <summary>Наибольшее число путей в одном запросе.</summary>
    public const int MaxPaths = 1_000;

    /// <summary>Абсолютные пути файлов и папок.</summary>
    public IReadOnlyList<string> Paths { get; set; } = [];
}

/// <summary>
/// Записи индекса для путей запроса.
/// </summary>
/// <param name="Entries">Найденные записи в порядке путей запроса, без повторов.</param>
/// <param name="Missing">Пути, которых нет в индексе или которые лежат вне корней, в исходном написании.</param>
public sealed record ResolveEntriesResponse(IReadOnlyList<EntryDto> Entries, IReadOnlyList<string> Missing);

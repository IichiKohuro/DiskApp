using DiskApp.UI.Text;

namespace DiskApp.UI.Models;

/// <summary>
/// Состояние индекса для строки состояния, трея и главной страницы.
/// </summary>
public sealed record IndexStatus
{
    /// <summary>Файлов и папок в индексе.</summary>
    public required long TotalEntries { get; init; }

    /// <summary>Корни индекса: папки и диски.</summary>
    public required IReadOnlyList<string> Roots { get; init; }

    /// <summary>Идёт ли индексация прямо сейчас.</summary>
    public bool IsIndexing { get; init; }

    /// <summary>
    /// Доля выполнения индексации от 0 до 1; <c>null</c> — объём заранее неизвестен (первое сканирование), есть только
    /// <see cref="Scanned"/>. Имеет смысл при <see cref="IsIndexing"/>.
    /// </summary>
    public double? Progress { get; init; }

    /// <summary>Сколько записей просмотрено в текущем проходе индексации.</summary>
    public long Scanned { get; init; }

    /// <summary>Когда индекс последний раз обновлялся.</summary>
    public DateTimeOffset LastUpdated { get; init; }

    /// <summary>Файлов во «входящих», ожидающих разбора.</summary>
    public int InboxCount { get; init; }

    /// <summary>
    /// Короткая подпись вида «128 412 файлов в 4 корнях · обновлён 2 мин назад» или «индексация 43 %».
    /// </summary>
    public string Describe(DateTimeOffset now)
    {
        var entries = Formatting.Count(TotalEntries, "файл", "файла", "файлов");
        var roots = Formatting.Count(Roots.Count, "корне", "корнях", "корнях");

        return IsIndexing
            ? $"{DescribeIndexing()} · {entries} в {roots}"
            : $"{entries} в {roots} · обновлён {Formatting.RelativeTime(LastUpdated, now)}";
    }

    /// <summary>
    /// Ход индексации: «индексация 43 %», если объём известен, иначе «индексация: просмотрено 12 345».
    /// </summary>
    public string DescribeIndexing() => Progress is { } progress
        ? $"индексация {Formatting.Percent(progress)}"
        : Scanned > 0 ? $"индексация: просмотрено {Formatting.Number(Scanned)}" : "идёт индексация";
}

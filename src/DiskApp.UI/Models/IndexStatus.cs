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

    /// <summary>Доля выполнения индексации от 0 до 1; имеет смысл при <see cref="IsIndexing"/>.</summary>
    public double Progress { get; init; }

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
            ? $"индексация {Formatting.Percent(Progress)} · {entries} в {roots}"
            : $"{entries} в {roots} · обновлён {Formatting.RelativeTime(LastUpdated, now)}";
    }
}

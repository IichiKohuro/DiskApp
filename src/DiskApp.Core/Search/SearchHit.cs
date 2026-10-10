using DiskApp.Core.Domain;

namespace DiskApp.Core.Search;

/// <summary>
/// Результат поиска: запись, её оценка, диапазоны подсветки и объяснение ранга для отладки.
/// </summary>
/// <param name="Entry">Запись индекса.</param>
/// <param name="Score">Оценка ранжирования; больше — выше.</param>
/// <param name="NameHighlights">Диапазоны совпадений в имени.</param>
/// <param name="PathHighlights">Диапазоны совпадений в относительном пути.</param>
/// <param name="RankReason">Короткое объяснение оценки («начало слова, frecency 2,1»); <c>null</c>, если отладка выключена.</param>
public sealed record SearchHit(
    FileEntry Entry,
    double Score,
    IReadOnlyList<TextRange> NameHighlights,
    IReadOnlyList<TextRange> PathHighlights,
    string? RankReason = null)
{
    /// <summary>
    /// Результат без подсветки.
    /// </summary>
    public static SearchHit Plain(FileEntry entry, double score) => new(entry, score, [], []);
}

/// <summary>
/// Диапазон символов для подсветки совпадения.
/// </summary>
public readonly record struct TextRange
{
    /// <summary>
    /// Создаёт диапазон.
    /// </summary>
    /// <param name="start">Индекс первого символа, от нуля.</param>
    /// <param name="length">Число символов, больше нуля.</param>
    public TextRange(int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);

        Start = start;
        Length = length;
    }

    /// <summary>Индекс первого символа.</summary>
    public int Start { get; }

    /// <summary>Число символов.</summary>
    public int Length { get; }

    /// <summary>Индекс за последним символом.</summary>
    public int End => Start + Length;

    /// <summary>Пересекается ли с другим диапазоном.</summary>
    public bool Overlaps(TextRange other) => Start < other.End && other.Start < End;
}

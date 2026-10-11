using System.Globalization;
using System.Text;

namespace DiskApp.Core.Search;

/// <summary>
/// Свёртка текста для поиска: нижний регистр по правилам .NET и «ё» → «е» (PLAN.md, этап 3.4), символ за символом.
/// Тем же правилом заполняется колонка <c>name_folded</c>, по которой строятся FTS и префиксный поиск.
/// </summary>
/// <remarks>
/// Имена в разложенной форме Unicode (NFD: с macOS, из архивов, с сетевых дисков) сначала собираются в готовые
/// символы, как в запросе с клавиатуры (этап 10.3): <c>e</c> + U+0301 → <c>é</c>. Такая свёртка короче исходного
/// имени, и подсветка переводится в его позиции через <see cref="FoldMapped"/>. Запрос перед свёрткой приводится
/// к составной форме (<see cref="Compose"/>).
/// </remarks>
public static class SearchText
{
    /// <summary>
    /// Версия правил свёртки: при её росте колонка <c>name_folded</c> пересчитывается для затронутых имён.
    /// 2 — сборка разложенных последовательностей Unicode (этап 10.3).
    /// </summary>
    public const int FoldVersion = 2;

    /// <summary>
    /// Свёрнутый текст. Для текста без разложенных последовательностей длина и позиции сохраняются.
    /// </summary>
    public static string Fold(string text) => FoldMapped(text).Folded;

    /// <summary>
    /// Свёрнутый текст вместе с собранным, но не свёрнутым текстом той же длины (для границ слов) и картой позиций
    /// в исходном тексте (для подсветки).
    /// </summary>
    public static FoldedText FoldMapped(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (!HasCombiningSequence(text))
        {
            return new FoldedText(string.Create(text.Length, text, static (span, source) =>
            {
                for (var i = 0; i < source.Length; i++)
                {
                    span[i] = FoldChar(source[i]);
                }
            }), text, null);
        }

        var composed = new StringBuilder(text.Length);
        var map = new List<int>(text.Length + 1);
        var i = 0;
        while (i < text.Length)
        {
            var end = i + 1;
            while (end < text.Length && IsCombining(text[end]))
            {
                end++;
            }

            var cluster = end - i > 1 ? TryCompose(text.AsSpan(i, end - i)) : null;
            if (cluster is null)
            {
                composed.Append(text[i]);
                map.Add(i);
                i++;
                continue;
            }

            // Символы собранного кластера указывают на его начало: подсветка захватывает кластер целиком.
            foreach (var character in cluster)
            {
                composed.Append(character);
                map.Add(i);
            }

            i = end;
        }

        map.Add(text.Length);
        var original = composed.ToString();
        return new FoldedText(string.Create(original.Length, original, static (span, source) =>
        {
            for (var k = 0; k < source.Length; k++)
            {
                span[k] = FoldChar(source[k]);
            }
        }), original, [.. map]);
    }

    /// <summary>
    /// Текст запроса в составной форме Unicode (NFC), чтобы разложенный ввод совпадал со свёрнутыми именами.
    /// Строка с недопустимыми суррогатами возвращается как есть.
    /// </summary>
    public static string Compose(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        try
        {
            return text.IsNormalized(NormalizationForm.FormC) ? text : text.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return text;
        }
    }

    /// <summary>
    /// Граница слова перед позицией <paramref name="index"/>: начало строки, разделитель (пробел, <c>_ - . ( [</c> и т. п.),
    /// переход из нижнего регистра в верхний (camelCase) или между буквой и цифрой. Проверяется по исходному тексту.
    /// </summary>
    public static bool IsWordStart(string original, int index)
    {
        ArgumentNullException.ThrowIfNull(original);

        if (index <= 0)
        {
            return true;
        }

        if (index >= original.Length)
        {
            return false;
        }

        var previous = original[index - 1];
        var current = original[index];
        if (!char.IsLetterOrDigit(previous))
        {
            return char.IsLetterOrDigit(current);
        }

        return (char.IsLower(previous) && char.IsUpper(current))
            || (char.IsLetter(previous) && char.IsDigit(current))
            || (char.IsDigit(previous) && char.IsLetter(current));
    }

    private static char FoldChar(char character)
    {
        var lower = char.ToLowerInvariant(character);
        return lower == 'ё' ? 'е' : lower;
    }

    private static bool HasCombiningSequence(string text)
    {
        for (var i = 1; i < text.Length; i++)
        {
            if (IsCombining(text[i]))
            {
                return true;
            }
        }

        return false;
    }

    // Комбинирующие знаки, а также гласные и конечные согласные хангыля: в NFD они идут после базового символа.
    private static bool IsCombining(char character)
        => character >= '\u0300'
            && ((character >= '\u1160' && character <= '\u11FF')
                || CharUnicodeInfo.GetUnicodeCategory(character) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark);

    private static string? TryCompose(ReadOnlySpan<char> cluster)
    {
        try
        {
            var composed = new string(cluster).Normalize(NormalizationForm.FormC);
            return composed.Length < cluster.Length ? composed : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// Свёрнутый текст с картой позиций (<see cref="SearchText.FoldMapped"/>).
/// </summary>
/// <param name="Folded">Свёрнутый текст.</param>
/// <param name="Composed">Собранный, но не свёрнутый текст той же длины, что <paramref name="Folded"/>: по нему ищутся границы слов.</param>
/// <param name="SourceIndex">
/// Для каждой позиции свёрнутого текста — позиция в исходном, последний элемент — длина исходного;
/// <c>null</c>, если позиции совпадают.
/// </param>
public readonly record struct FoldedText(string Folded, string Composed, int[]? SourceIndex)
{
    /// <summary>Диапазон свёрнутого текста в позициях исходного.</summary>
    public TextRange ToSource(TextRange range)
    {
        if (SourceIndex is not { } map)
        {
            return range;
        }

        var start = map[range.Start];
        var end = map[range.End];

        // Конец указывает на начало следующего символа; символы одного кластера ведут к его началу.
        if (end <= start)
        {
            end = start + 1;
        }

        return new TextRange(start, end - start);
    }
}

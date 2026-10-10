namespace DiskApp.Core.Search;

/// <summary>
/// Свёртка текста для поиска: нижний регистр по правилам .NET и «ё» → «е» (PLAN.md, этап 3.4), символ за символом,
/// поэтому длина и позиции сохраняются — диапазоны подсветки, найденные в свёрнутом имени, подходят к исходному.
/// Тем же правилом заполняется колонка <c>name_folded</c>, по которой строятся FTS и префиксный поиск.
/// </summary>
public static class SearchText
{
    /// <summary>
    /// Свёрнутый текст той же длины.
    /// </summary>
    public static string Fold(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return string.Create(text.Length, text, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var character = char.ToLowerInvariant(source[i]);
                span[i] = character == 'ё' ? 'е' : character;
            }
        });
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
}

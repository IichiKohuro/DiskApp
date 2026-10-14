using System.Text;

namespace DiskApp.Core.Sorting;

/// <summary>
/// Признаки имени для подсказок рабочей папки (PLAN.md, этап 17.4): слова имени и расширение. Слово — подряд идущие буквы
/// или подряд идущие цифры, в нижнем регистре, «ё» как «е»; разделители, регистр, числа и даты сравнению не мешают. Из чисел
/// остаётся только похожее на номер проекта или договора — 3–4 цифры без ведущего нуля, не год: «417» в «10S-417 протокол»
/// говорит о папке, а «12102026» и «2026» — нет.
/// </summary>
public static class SortingFeatures
{
    /// <summary>Сколько слов имени учитывать: первые по порядку.</summary>
    public const int MaxWords = 8;

    // Предлоги, союзы и «копия» из «Копия отчёт.docx» — в любом имени, о папке ничего не говорят.
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "и", "в", "во", "на", "по", "из", "до", "от", "за", "не", "для", "с", "со", "к", "ко", "о", "об", "копия",
        "the", "of", "and", "for", "to", "in", "on", "at", "by", "an", "copy",
    };

    /// <summary>Слова имени без повторов, не больше <see cref="MaxWords"/>.</summary>
    /// <param name="name">Имя файла или папки.</param>
    /// <param name="isFolder">Это папка: точка в имени не отделяет расширение.</param>
    public static IReadOnlyList<string> Words(string name, bool isFolder)
    {
        ArgumentNullException.ThrowIfNull(name);

        var stem = isFolder ? name : Path.GetFileNameWithoutExtension(name);
        var words = new List<string>();
        var token = new StringBuilder();
        var digits = false;
        foreach (var symbol in stem.Append(' '))
        {
            var isLetter = char.IsLetter(symbol);
            var isDigit = char.IsAsciiDigit(symbol);
            if (token.Length > 0 && (!(isLetter || isDigit) || isDigit != digits))
            {
                var word = token.ToString();
                token.Clear();
                if (Keeps(word, digits) && !words.Contains(word))
                {
                    words.Add(word);
                    if (words.Count == MaxWords)
                    {
                        break;
                    }
                }
            }

            if (isLetter || isDigit)
            {
                digits = isDigit;
                token.Append(symbol == 'ё' || symbol == 'Ё' ? 'е' : char.ToLowerInvariant(symbol));
            }
        }

        return words;
    }

    /// <summary>Расширение в нижнем регистре без точки; у папки и файла без расширения — пусто.</summary>
    public static string Extension(string name, bool isFolder)
    {
        ArgumentNullException.ThrowIfNull(name);
        return isFolder ? string.Empty : SortingRuleSet.NormalizeExtension(Path.GetExtension(name));
    }

    private static bool Keeps(string word, bool digits)
    {
        if (!digits)
        {
            return word.Length >= 2 && !StopWords.Contains(word);
        }

        // Номер — да, порядковый номер с нулями, день, месяц, год и слитная дата — нет.
        return word.Length is 3 or 4 && word[0] != '0' && !(word.Length == 4 && (word.StartsWith("19", StringComparison.Ordinal) || word.StartsWith("20", StringComparison.Ordinal)));
    }
}

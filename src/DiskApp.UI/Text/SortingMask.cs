using System.Buffers;

namespace DiskApp.UI.Text;

/// <summary>
/// Маска шаблона по именам файлов (PLAN.md, этап 17.5, «Всегда класть такие файлы сюда?»): общее начало имён до
/// разделителя (<c>417_ТЗ.pdf</c> и <c>417_акт.docx</c> → <c>417_*</c>), у одного файла — первое слово с разделителем
/// (<c>Пароли Brave.csv</c> → <c>Пароли *</c>), без общего начала — общее расширение (<c>*.pdf</c>). Пользователь правит её
/// в диалоге, видя, сколько файлов подходит.
/// </summary>
public static class SortingMask
{
    private static readonly SearchValues<char> Separators = SearchValues.Create("_- .([");

    /// <summary>
    /// Предлагает маску для этих имён.
    /// </summary>
    /// <param name="names">Имена с расширением.</param>
    /// <returns>Маска; пусто — имён нет.</returns>
    public static string Suggest(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var stems = names.Where(static name => !string.IsNullOrWhiteSpace(name)).Select(static name => Path.GetFileNameWithoutExtension(name.Trim())).ToArray();
        if (stems.Length == 0)
        {
            return string.Empty;
        }

        var prefix = stems.Length == 1 ? FirstWord(stems[0]) : CommonStart(stems);

        // Общее расширение — только для нескольких файлов: у одного «*.pdf» забрало бы все PDF.
        if (prefix.Trim().Length >= 2 || stems.Length == 1)
        {
            return prefix + "*";
        }

        var extensions = names.Select(static name => Path.GetExtension(name.Trim())).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return extensions is [{ Length: > 1 } extension] ? "*" + extension.ToLowerInvariant() : FirstWord(stems[0]) + "*";
    }

    // Первое слово с разделителем после него; без разделителя — имя без цифр в конце («invoice123» → «invoice»).
    private static string FirstWord(string stem)
    {
        // Разделитель ищется со второго символа: имя «_черновик» не даёт маску «_*».
        var separator = stem.Length > 1 ? stem.AsSpan(1).IndexOfAny(Separators) : -1;
        if (separator >= 0)
        {
            return stem[..(separator + 2)];
        }

        var trimmed = stem.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return trimmed.Length >= 2 ? trimmed : stem;
    }

    // Общее начало без учёта регистра, обрезанное по последнему разделителю: «Счёт 12» и «Счёт 13» → «Счёт ».
    private static string CommonStart(string[] stems)
    {
        var length = stems.Min(static stem => stem.Length);
        var common = 0;
        while (common < length && stems.All(stem => char.ToUpperInvariant(stem[common]) == char.ToUpperInvariant(stems[0][common])))
        {
            common++;
        }

        var prefix = stems[0][..common];
        if (common == stems[0].Length || stems.Any(stem => stem.Length == common))
        {
            return prefix;
        }

        var cut = prefix.AsSpan().LastIndexOfAny(Separators);
        return cut > 0 ? prefix[..(cut + 1)] : prefix;
    }
}

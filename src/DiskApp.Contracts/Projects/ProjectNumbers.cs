namespace DiskApp.Contracts.Projects;

/// <summary>Номера проектов: сравнение для списка и совпадение без учёта регистра и «ё».</summary>
public static class ProjectNumbers
{
    /// <summary>Длиннее нельзя: номер должен читаться на иконке.</summary>
    public const int MaxLength = 20;

    /// <summary>Один и тот же номер: без пробелов по краям, регистра и разницы «ё»/«е».</summary>
    public static bool Same(string? left, string? right) => string.Equals(Fold(left), Fold(right), StringComparison.Ordinal);

    /// <summary>Сравнение для списка: числа внутри — как числа («2» раньше «10», «417» после «99»), без учёта регистра.</summary>
    public static int Compare(string? left, string? right)
    {
        var a = left ?? string.Empty;
        var b = right ?? string.Empty;
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                var startA = i;
                var startB = j;
                while (i < a.Length && char.IsAsciiDigit(a[i]))
                {
                    i++;
                }

                while (j < b.Length && char.IsAsciiDigit(b[j]))
                {
                    j++;
                }

                var digitsA = a[startA..i].TrimStart('0');
                var digitsB = b[startB..j].TrimStart('0');
                var byNumber = digitsA.Length != digitsB.Length ? digitsA.Length.CompareTo(digitsB.Length) : string.CompareOrdinal(digitsA, digitsB);
                if (byNumber != 0)
                {
                    return byNumber;
                }
            }
            else
            {
                var byChar = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                if (byChar != 0)
                {
                    return byChar;
                }

                i++;
                j++;
            }
        }

        return (a.Length - i).CompareTo(b.Length - j);
    }

    private static string Fold(string? number) => (number ?? string.Empty).Trim().ToLowerInvariant().Replace('ё', 'е');
}

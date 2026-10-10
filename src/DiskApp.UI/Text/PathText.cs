namespace DiskApp.UI.Text;

/// <summary>
/// Разбор путей как текста, без обращения к файловой системе и без привязки к разделителю текущей ОС:
/// индекс может содержать пути Windows (<c>\</c>) и POSIX (<c>/</c>), а интерфейс лишь показывает их.
/// </summary>
public static class PathText
{
    private const string Ellipsis = "…";
    private static readonly char[] Separators = ['\\', '/'];

    /// <summary>
    /// Имя файла или папки: часть после последнего разделителя. Для корня диска (<c>D:\</c>) — сам корень.
    /// </summary>
    public static string FileName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var trimmed = path.TrimEnd(Separators);
        if (trimmed.Length == 0)
        {
            return path;
        }

        var index = trimmed.LastIndexOfAny(Separators);
        if (index >= 0)
        {
            return trimmed[(index + 1)..];
        }

        // «D:\» — корень диска: показываем его целиком, как Проводник.
        return IsDriveLetter(trimmed) ? path : trimmed;
    }

    /// <summary>
    /// Папка, в которой лежит путь, без завершающего разделителя. Для корня диска — пусто.
    /// </summary>
    public static string DirectoryName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var trimmed = path.TrimEnd(Separators);
        var index = trimmed.LastIndexOfAny(Separators);
        if (index <= 0)
        {
            return string.Empty;
        }

        var directory = trimmed[..index];

        // «D:» без разделителя — не папка; возвращаем «D:\», как показывает Проводник.
        return IsDriveLetter(directory) ? directory + trimmed[index] : directory;
    }

    private static bool IsDriveLetter(string text) => text.Length == 2 && text[1] == ':' && char.IsLetter(text[0]);

    /// <summary>
    /// Расширение без точки в нижнем регистре; пусто, если его нет или имя начинается с точки (<c>.gitignore</c>).
    /// </summary>
    public static string Extension(string path)
    {
        var name = FileName(path);
        var dot = name.LastIndexOf('.');
        return dot <= 0 || dot == name.Length - 1 ? string.Empty : name[(dot + 1)..].ToLowerInvariant();
    }

    /// <summary>
    /// Сокращает путь до <paramref name="maxLength"/> символов, выбрасывая сегменты из середины:
    /// <c>D:\Projects\…\src\Program.cs</c>. Начало и конец сохраняются, чтобы путь оставался узнаваемым.
    /// </summary>
    public static string Shorten(string path, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 8);

        if (path.Length <= maxLength)
        {
            return path;
        }

        var separatorIndex = path.IndexOfAny(Separators);
        var separator = separatorIndex >= 0 ? path[separatorIndex] : '\\';
        var segments = path.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3)
        {
            return TruncateMiddle(path, maxLength);
        }

        // Берём первый сегмент и столько последних, сколько влезает.
        var head = segments[0];
        var tail = new List<string>();
        var length = head.Length + 1 + Ellipsis.Length;
        for (var i = segments.Length - 1; i > 0; i--)
        {
            var candidate = length + 1 + segments[i].Length;
            if (candidate > maxLength && tail.Count > 0)
            {
                break;
            }

            tail.Insert(0, segments[i]);
            length = candidate;
        }

        var result = $"{head}{separator}{Ellipsis}{separator}{string.Join(separator, tail)}";
        return result.Length <= maxLength ? result : TruncateMiddle(result, maxLength);
    }

    private static string TruncateMiddle(string text, int maxLength)
    {
        var keep = maxLength - Ellipsis.Length;
        var headLength = keep / 2;
        var tailLength = keep - headLength;
        return text[..headLength] + Ellipsis + text[^tailLength..];
    }
}

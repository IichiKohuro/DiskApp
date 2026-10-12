using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DiskApp.UI.Text;

/// <summary>Кнопка панели форматирования заметки (PLAN.md, этап 23).</summary>
public enum MarkdownFormat
{
    /// <summary>Заголовок: <c>#</c> → <c>##</c> → <c>###</c> → обычный текст.</summary>
    Heading,

    /// <summary><c>**жирный**</c>.</summary>
    Bold,

    /// <summary><c>*курсив*</c>.</summary>
    Italic,

    /// <summary><c>~~зачёркнутый~~</c>.</summary>
    Strikethrough,

    /// <summary><c>- пункт</c>.</summary>
    BulletList,

    /// <summary><c>1. пункт</c>.</summary>
    NumberedList,

    /// <summary><c>- [ ] дело</c>.</summary>
    TaskList,

    /// <summary><c>&gt; цитата</c>.</summary>
    Quote,

    /// <summary><c>`код`</c>, несколько строк — блоком <c>```</c>.</summary>
    Code,

    /// <summary><c>[текст](адрес)</c>.</summary>
    Link,

    /// <summary>Разделитель <c>---</c>.</summary>
    Divider,
}

/// <summary>
/// Правка текста: заменить <paramref name="Length"/> символов с <paramref name="Start"/> на <paramref name="Replacement"/>,
/// затем выделить от <paramref name="SelectionStart"/> до <paramref name="SelectionEnd"/>. Одна замена — один шаг отмены.
/// </summary>
/// <param name="Start">Начало заменяемого.</param>
/// <param name="Length">Сколько символов заменить.</param>
/// <param name="Replacement">Новый текст.</param>
/// <param name="SelectionStart">Начало выделения после правки.</param>
/// <param name="SelectionEnd">Конец выделения после правки.</param>
public sealed record MarkdownEdit(int Start, int Length, string Replacement, int SelectionStart, int SelectionEnd)
{
    /// <summary>Текст после правки.</summary>
    public string ApplyTo(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return string.Concat(text.AsSpan(0, Start), Replacement, text.AsSpan(Start + Length));
    }
}

/// <summary>
/// Панель форматирования заметки (PLAN.md, этап 23; просьба пользователя «чтобы было просто и удобно»): кнопки работают с
/// выделением — оборачивают его или убирают обёртку, ставят и снимают префикс строк; <c>Enter</c> в пункте списка
/// продолжает список, на пустом пункте — заканчивает его. Только текст: никакой разметки не прячется.
/// </summary>
public static partial class MarkdownEditing
{
    private const int MaxHeadingLevel = 3;

    /// <summary>Правка для кнопки панели.</summary>
    /// <param name="text">Текст заметки.</param>
    /// <param name="selectionStart">Начало выделения (или место курсора).</param>
    /// <param name="selectionEnd">Конец выделения.</param>
    /// <param name="format">Кнопка.</param>
    public static MarkdownEdit Apply(string? text, int selectionStart, int selectionEnd, MarkdownFormat format)
    {
        text ??= string.Empty;
        var start = Math.Clamp(Math.Min(selectionStart, selectionEnd), 0, text.Length);
        var end = Math.Clamp(Math.Max(selectionStart, selectionEnd), 0, text.Length);
        return format switch
        {
            MarkdownFormat.Bold => Wrap(text, start, end, "**"),
            MarkdownFormat.Italic => Wrap(text, start, end, "*"),
            MarkdownFormat.Strikethrough => Wrap(text, start, end, "~~"),
            MarkdownFormat.Code => text.AsSpan(start, end - start).Contains('\n') ? Fence(text, start, end) : Wrap(text, start, end, "`"),
            MarkdownFormat.Heading => Heading(text, start, end),
            MarkdownFormat.BulletList => Prefix(text, start, end, LineKind.Bullet),
            MarkdownFormat.NumberedList => Prefix(text, start, end, LineKind.Numbered),
            MarkdownFormat.TaskList => Prefix(text, start, end, LineKind.Task),
            MarkdownFormat.Quote => Prefix(text, start, end, LineKind.Quote),
            MarkdownFormat.Link => Link(text, start, end),
            MarkdownFormat.Divider => Divider(text, start, end),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
        };
    }

    /// <summary>
    /// <c>Enter</c> в пункте списка или цитаты: новая строка с тем же маркером (номер — следующий, флажок — пустой);
    /// в пустом пункте — маркер убирается, список закончен. <c>null</c> — строка не пункт, <c>Enter</c> обычный.
    /// </summary>
    /// <param name="text">Текст заметки.</param>
    /// <param name="caret">Место курсора.</param>
    public static MarkdownEdit? ContinueList(string? text, int caret)
    {
        text ??= string.Empty;
        caret = Math.Clamp(caret, 0, text.Length);
        var (lineStart, lineEnd) = LineAt(text, caret);
        var line = text[lineStart..lineEnd].TrimEnd('\r');
        var marker = LinePrefix().Match(line);
        if (!marker.Groups["marker"].Success)
        {
            return null;
        }

        var indent = marker.Groups["indent"].Value;
        var prefixEnd = lineStart + marker.Length;
        if (caret < prefixEnd)
        {
            return null;
        }

        if (line.Length == marker.Length)
        {
            // Пустой пункт: Enter заканчивает список — маркер убирается, курсор остаётся на этой строке.
            return new MarkdownEdit(lineStart, marker.Length, string.Empty, lineStart, lineStart);
        }

        var kind = KindOf(marker.Groups["marker"].Value);
        var next = kind == LineKind.Numbered && int.TryParse(marker.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? $"{number + 1}{marker.Groups["dot"].Value} "
            : MarkerOf(kind, 0);
        var insert = NewLineOf(text) + indent + next;
        return new MarkdownEdit(caret, 0, insert, caret + insert.Length, caret + insert.Length);
    }

    [GeneratedRegex(@"^(?<indent>[ \t]*)(?<marker>[-*+] \[[ xX]\] |[-*+] |(?<number>\d{1,9})(?<dot>[.)]) |> )?")]
    private static partial Regex LinePrefix();

    [GeneratedRegex(@"^(https?://|www\.|mailto:|[A-Za-z]:\\|\\\\)", RegexOptions.IgnoreCase)]
    private static partial Regex Address();

    [GeneratedRegex(@"^(?<hashes>#{1,6})(?<space>[ \t]*)")]
    private static partial Regex HeadingPrefix();

    private static MarkdownEdit Wrap(string text, int start, int end, string marker)
    {
        var length = marker.Length;
        if (start == end)
        {
            // Без выделения — пара маркеров, курсор между ними: дальше просто печатать.
            return new MarkdownEdit(start, 0, marker + marker, start + length, start + length);
        }

        var selected = text[start..end];
        if (selected.Length >= 2 * length && selected.StartsWith(marker, StringComparison.Ordinal) && selected.EndsWith(marker, StringComparison.Ordinal)
            && IsWholeMarker(selected, 0, marker) && IsWholeMarker(selected, selected.Length - length, marker))
        {
            var inner = selected[length..^length];
            return new MarkdownEdit(start, end - start, inner, start, start + inner.Length);
        }

        if (start >= length && end + length <= text.Length && string.CompareOrdinal(text, start - length, marker, 0, length) == 0
            && string.CompareOrdinal(text, end, marker, 0, length) == 0 && IsWholeMarker(text, start - length, marker) && IsWholeMarker(text, end, marker))
        {
            // Выделение уже обёрнуто: повторное нажатие снимает обёртку.
            return new MarkdownEdit(start - length, end - start + 2 * length, selected, start - length, end - length);
        }

        // Пробелы по краям выделения остаются снаружи: «** жирный**» Markdown не распознаёт.
        var trimmedStart = start + (selected.Length - selected.TrimStart().Length);
        var trimmedEnd = end - (selected.Length - selected.TrimEnd().Length);
        if (trimmedStart >= trimmedEnd)
        {
            return new MarkdownEdit(end, 0, marker + marker, end + length, end + length);
        }

        var core = text[trimmedStart..trimmedEnd];
        return new MarkdownEdit(trimmedStart, trimmedEnd - trimmedStart, marker + core + marker, trimmedStart + length, trimmedEnd + length);
    }

    // «*» курсива не путается со «**» жирного: маркер — ровно эти символы, а не часть более длинной серии.
    private static bool IsWholeMarker(string text, int index, string marker)
    {
        var character = marker[0];
        var before = index > 0 && text[index - 1] == character;
        var after = index + marker.Length < text.Length && text[index + marker.Length] == character;
        return !before && !after;
    }

    private static MarkdownEdit Fence(string text, int start, int end)
    {
        var newLine = NewLineOf(text);
        var (blockStart, _) = LineAt(text, start);
        var before = start > blockStart ? newLine : string.Empty;
        var inner = text[start..end].TrimEnd('\r', '\n');
        var replacement = before + "```" + newLine + inner + newLine + "```";
        var innerStart = start + before.Length + 3 + newLine.Length;
        return new MarkdownEdit(start, end - start, replacement, innerStart, innerStart + inner.Length);
    }

    private static MarkdownEdit Heading(string text, int start, int end)
    {
        var (lineStart, lineEnd) = LineAt(text, start);
        var line = text[lineStart..lineEnd].TrimEnd('\r');
        var heading = HeadingPrefix().Match(line);
        var content = heading.Success ? line[heading.Length..] : line.TrimStart();
        var level = heading.Success ? heading.Groups["hashes"].Length : 0;
        string prefix;
        if (heading.Success && heading.Groups["space"].Length == 0 && content.Length > 0)
        {
            // «#Cisco» — уже заголовок по замыслу, только без пробела: пробел добавляется, уровень тот же.
            prefix = new string('#', level) + " ";
        }
        else
        {
            var next = level >= MaxHeadingLevel ? 0 : level + 1;
            prefix = next == 0 ? string.Empty : new string('#', next) + " ";
        }

        var replacement = prefix + content;
        var delta = replacement.Length - line.Length;
        var selectionStart = Math.Clamp(start + delta, lineStart + prefix.Length, lineStart + replacement.Length);
        var selectionEnd = Math.Max(selectionStart, end + delta);
        return new MarkdownEdit(lineStart, line.Length, replacement, selectionStart, selectionEnd);
    }

    private static MarkdownEdit Prefix(string text, int start, int end, LineKind kind)
    {
        var (blockStart, _) = LineAt(text, start);
        var lastLine = end > start && text[end - 1] == '\n' ? end - 1 : end;
        var (_, blockEnd) = LineAt(text, lastLine);
        var lines = text[blockStart..blockEnd].Split('\n');
        var parsed = lines.Select(static raw =>
        {
            var carriage = raw.EndsWith('\r') ? "\r" : string.Empty;
            var line = raw[..^carriage.Length];
            var match = LinePrefix().Match(line);
            return (Line: line, Carriage: carriage, Match: match, Kind: match.Groups["marker"].Success ? KindOf(match.Groups["marker"].Value) : LineKind.None);
        }).ToArray();

        // Все непустые строки уже такие — кнопка снимает маркер; иначе ставит его, заменяя другой маркер списка.
        var remove = parsed.Where(static line => line.Line.Trim().Length > 0).All(line => line.Kind == kind)
            && parsed.Any(static line => line.Line.Trim().Length > 0);
        var builder = new StringBuilder();
        var number = 0;
        for (var i = 0; i < parsed.Length; i++)
        {
            var (line, carriage, match, _) = parsed[i];
            if (i > 0)
            {
                builder.Append('\n');
            }

            var indent = match.Groups["indent"].Value;
            var content = line[match.Length..];
            if (line.Trim().Length == 0 && parsed.Length > 1)
            {
                builder.Append(line).Append(carriage);
                continue;
            }

            builder.Append(indent);
            if (!remove)
            {
                builder.Append(MarkerOf(kind, ++number));
            }

            builder.Append(content).Append(carriage);
        }

        var replacement = builder.ToString();
        if (start == end && parsed.Length == 1)
        {
            // Курсор остаётся на своём месте в тексте строки.
            var delta = replacement.Length - (blockEnd - blockStart);
            var caret = Math.Max(blockStart + (replacement.Length - parsed[0].Line[parsed[0].Match.Length..].Length - parsed[0].Carriage.Length), start + delta);
            caret = Math.Min(caret, blockStart + replacement.Length - parsed[0].Carriage.Length);
            return new MarkdownEdit(blockStart, blockEnd - blockStart, replacement, caret, caret);
        }

        return new MarkdownEdit(blockStart, blockEnd - blockStart, replacement, blockStart, blockStart + replacement.Length);
    }

    private static MarkdownEdit Link(string text, int start, int end)
    {
        var selected = text[start..end].Trim();
        if (selected.Length == 0)
        {
            return new MarkdownEdit(start, end - start, "[]()", start + 1, start + 1);
        }

        if (Address().IsMatch(selected))
        {
            // Выделен адрес или путь — он становится ссылкой, курсор — на месте подписи.
            var replacement = "[](" + selected + ")";
            return new MarkdownEdit(start, end - start, replacement, start + 1, start + 1);
        }

        var link = "[" + selected + "]()";
        var caret = start + link.Length - 1;
        return new MarkdownEdit(start, end - start, link, caret, caret);
    }

    // Перед «---» нужна пустая строка, иначе Markdown сделает из строки выше заголовок.
    private static MarkdownEdit Divider(string text, int start, int end)
    {
        var newLine = NewLineOf(text);
        var head = text[..start];
        var breaks = head[head.TrimEnd('\r', '\n').Length..].Count(static character => character == '\n');
        var before = head.Trim().Length == 0 || breaks >= 2 ? string.Empty : breaks == 1 ? newLine : newLine + newLine;
        var replacement = before + "---" + newLine;
        var caret = start + replacement.Length;
        return new MarkdownEdit(start, end - start, replacement, caret, caret);
    }

    private static (int Start, int End) LineAt(string text, int index)
    {
        var start = index > 0 ? text.LastIndexOf('\n', index - 1) + 1 : 0;
        var end = text.IndexOf('\n', index);
        return (start, end < 0 ? text.Length : end);
    }

    private static string NewLineOf(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static LineKind KindOf(string marker) => marker switch
    {
        _ when marker.EndsWith("] ", StringComparison.Ordinal) => LineKind.Task,
        "> " => LineKind.Quote,
        _ when char.IsAsciiDigit(marker[0]) => LineKind.Numbered,
        _ => LineKind.Bullet,
    };

    private static string MarkerOf(LineKind kind, int number) => kind switch
    {
        LineKind.Task => "- [ ] ",
        LineKind.Quote => "> ",
        LineKind.Numbered => number.ToString(CultureInfo.InvariantCulture) + ". ",
        _ => "- ",
    };

    private enum LineKind
    {
        None,
        Bullet,
        Numbered,
        Task,
        Quote,
    }
}

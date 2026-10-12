using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DiskApp.UI.Text;

/// <summary>
/// Что подставляется в шаблон имени при переносе.
/// </summary>
/// <param name="OriginalName">Прежнее имя с расширением.</param>
/// <param name="IsFolder">Это папка: расширения нет, шаблон применяется к имени целиком.</param>
/// <param name="Number">Номер в переносимой пачке, с единицы.</param>
/// <param name="Count">Сколько элементов в пачке.</param>
/// <param name="Tags">Теги переноса.</param>
/// <param name="FolderName">Имя папки назначения.</param>
/// <param name="Now">Момент переноса.</param>
public sealed record FileNameContext(string OriginalName, bool IsFolder, int Number, int Count, IReadOnlyList<string> Tags, string FolderName, DateTimeOffset Now);

/// <summary>
/// Шаблон имени при переносе (PLAN.md, этап 17): <c>{имя}</c> — прежнее имя без расширения, <c>{дата}</c> и <c>{время}</c> —
/// момент переноса без разделителей, как просил пользователь (<c>12102026</c>, <c>135445</c>;
/// вместе — <c>{дата}{время}</c> = <c>12102026135445</c>), <c>{теги}</c> — теги через «_», <c>{папка}</c> — папка назначения,
/// <c>{№}</c> — номер в пачке; те же подстановки по-английски: <c>{name}</c>, <c>{date}</c>, <c>{time}</c>, <c>{tags}</c>,
/// <c>{folder}</c>, <c>{n}</c>. Расширение сохраняется всегда. Шаблон без <c>{имя}</c> и <c>{№}</c> для нескольких файлов
/// получает номер в конце, чтобы имена не совпали. Недопустимые в Windows символы заменяются на «_».
/// </summary>
public static partial class FileNameTemplate
{
    /// <summary>Формат <c>{дата}</c>: день, месяц, год без разделителей — 12102026.</summary>
    public const string DateFormat = "ddMMyyyy";

    /// <summary>Формат <c>{время}</c>: часы, минуты, секунды без разделителей — 135445.</summary>
    public const string TimeFormat = "HHmmss";

    private const int MaxNameLength = 255;

    /// <summary>В шаблоне есть прежнее имя: такой шаблон годится для многих файлов и запоминается.</summary>
    public static bool HasNameToken(string? template)
        => template is not null && TokenPattern().Matches(template).Any(static match => Key(match) is "name");

    /// <summary>Новое имя по шаблону; пустой шаблон — прежнее имя.</summary>
    public static string Apply(string? template, FileNameContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (string.IsNullOrWhiteSpace(template))
        {
            return context.OriginalName;
        }

        var extension = context.IsFolder ? string.Empty : Path.GetExtension(context.OriginalName);
        var stem = context.IsFolder ? context.OriginalName : Path.GetFileNameWithoutExtension(context.OriginalName);
        var keys = TokenPattern().Matches(template).Select(Key).ToHashSet(StringComparer.Ordinal);
        var result = TokenPattern().Replace(template.Trim(), match => Key(match) switch
        {
            "name" => stem,
            "date" => context.Now.ToString(DateFormat, CultureInfo.InvariantCulture),
            "time" => context.Now.ToString(TimeFormat, CultureInfo.InvariantCulture),
            "tags" => string.Join("_", context.Tags.Select(static tag => tag.Trim()).Where(static tag => tag.Length > 0)),
            "folder" => context.FolderName,
            "n" => context.Number.ToString(CultureInfo.InvariantCulture),
            _ => match.Value,
        });

        if (context.Count > 1 && !keys.Contains("name") && !keys.Contains("n"))
        {
            result += " (" + context.Number.ToString(CultureInfo.InvariantCulture) + ")";
        }

        result = Clean(result);
        if (result.Length == 0)
        {
            result = stem;
        }

        if (result.Length + extension.Length > MaxNameLength)
        {
            result = result[..Math.Max(1, MaxNameLength - extension.Length)].TrimEnd(' ', '.');
        }

        return result + extension;
    }

    private static string Key(Match match) => match.Groups["key"].Value.ToLowerInvariant() switch
    {
        "имя" or "name" => "name",
        "дата" or "date" => "date",
        "время" or "time" => "time",
        "теги" or "tags" => "tags",
        "папка" or "folder" => "folder",
        "№" or "n" => "n",
        var other => other,
    };

    // Недопустимые символы → «_», повторы разделителей схлопываются, по краям — ни пробелов, ни точек, ни «_».
    private static string Clean(string name)
    {
        var builder = new StringBuilder(name.Length);
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var character in name)
        {
            builder.Append(character < ' ' || invalid.Contains(character) || character is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : character);
        }

        var cleaned = RepeatedSeparators().Replace(builder.ToString(), static match => match.Value.Contains('_', StringComparison.Ordinal) ? "_" : match.Value[..1]);
        return cleaned.Trim(' ', '.', '_', '-');
    }

    [GeneratedRegex(@"\{(?<key>имя|name|дата|date|время|time|теги|tags|папка|folder|№|n)\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"[ _]*_[ _]*|  +|--+")]
    private static partial Regex RepeatedSeparators();
}

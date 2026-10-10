using System.Collections.Frozen;

namespace DiskApp.Core.Domain;

/// <summary>
/// Таблица «расширение → вид файла». Встроенный набор покрывает повседневные форматы; пользователь переопределяет
/// отдельные расширения в настройках (<see cref="With"/>). Неизменяема, безопасна для общего использования.
/// </summary>
public sealed class FileKindMap
{
    private static readonly (FileKind Kind, string[] Extensions)[] BuiltIn =
    [
        (FileKind.Image, ["jpg", "jpeg", "png", "gif", "bmp", "webp", "tif", "tiff", "heic", "heif", "avif", "svg", "ico", "psd", "raw", "cr2", "cr3", "nef", "arw", "dng", "orf", "rw2"]),
        (FileKind.Video, ["mp4", "m4v", "mkv", "avi", "mov", "wmv", "flv", "webm", "mpg", "mpeg", "3gp", "mts", "m2ts", "vob"]),
        (FileKind.Audio, ["mp3", "wav", "flac", "aac", "ogg", "oga", "opus", "m4a", "wma", "aiff", "aif", "ape", "mid", "midi"]),
        (FileKind.Document, ["doc", "docx", "docm", "dot", "dotx", "odt", "ott", "rtf", "txt", "md", "markdown", "tex", "pages", "wps", "epub", "fb2", "djvu", "log"]),
        (FileKind.Spreadsheet, ["xls", "xlsx", "xlsm", "xlsb", "xlt", "ods", "ots", "csv", "tsv", "numbers"]),
        (FileKind.Presentation, ["ppt", "pptx", "pptm", "pps", "ppsx", "pot", "potx", "odp", "otp", "key"]),
        (FileKind.Pdf, ["pdf"]),
        (FileKind.Archive, ["zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "tbz2", "xz", "txz", "zst", "lz", "lzma", "cab", "iso", "img", "wim", "dmg"]),
        (FileKind.Code, ["cs", "csproj", "sln", "slnx", "props", "targets", "fs", "vb", "axaml", "xaml", "resx", "js", "mjs", "cjs", "ts", "tsx", "jsx", "json", "jsonc", "html", "htm", "css", "scss", "sass", "less", "xml", "xsd", "xslt", "yaml", "yml", "toml", "ini", "cfg", "conf", "env", "py", "ipynb", "java", "kt", "kts", "gradle", "go", "rs", "rb", "php", "swift", "m", "mm", "c", "h", "cc", "cpp", "cxx", "hpp", "hxx", "cmake", "sql", "ps1", "psm1", "psd1", "sh", "bash", "zsh", "bat", "cmd", "lua", "pl", "r", "dart", "scala", "groovy", "gitignore", "editorconfig", "dockerfile", "proto", "graphql", "vue", "svelte"]),
        (FileKind.Executable, ["exe", "msi", "msix", "msixbundle", "appx", "appxbundle", "dll", "com", "scr", "cpl", "sys", "jar", "apk", "app", "deb", "rpm", "appimage"]),
        (FileKind.Shortcut, ["lnk", "url", "webloc", "desktop"]),
    ];

    private readonly FrozenDictionary<string, FileKind> _byExtension;

    private FileKindMap(FrozenDictionary<string, FileKind> byExtension)
    {
        _byExtension = byExtension;
    }

    /// <summary>Встроенная таблица без переопределений.</summary>
    public static FileKindMap Default { get; } = new(BuildDefault());

    /// <summary>Сколько расширений известно таблице.</summary>
    public int Count => _byExtension.Count;

    /// <summary>
    /// Вид по расширению. Папка всегда <see cref="FileKind.Folder"/>; неизвестное или пустое расширение — <see cref="FileKind.Other"/>.
    /// </summary>
    /// <param name="extension">Расширение с точкой или без, в любом регистре.</param>
    /// <param name="isDirectory">Запись — папка.</param>
    public FileKind Classify(string? extension, bool isDirectory = false)
    {
        if (isDirectory)
        {
            return FileKind.Folder;
        }

        if (string.IsNullOrEmpty(extension))
        {
            return FileKind.Other;
        }

        var key = extension[0] == '.' ? extension[1..] : extension;
        return _byExtension.TryGetValue(key, out var kind) ? kind : FileKind.Other;
    }

    /// <summary>
    /// Известно ли расширение таблице.
    /// </summary>
    public bool Contains(string extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        return _byExtension.ContainsKey(extension[0] == '.' ? extension[1..] : extension);
    }

    /// <summary>
    /// Все расширения указанного вида в алфавитном порядке; для <see cref="FileKind.Folder"/> и <see cref="FileKind.Other"/> — пусто.
    /// </summary>
    public IReadOnlyList<string> ExtensionsOf(FileKind kind)
        => _byExtension
            .Where(pair => pair.Value == kind)
            .Select(static pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// Новая таблица с переопределениями поверх текущей: расширение можно переназначить на другой вид
    /// или добавить неизвестное. Переопределение на <see cref="FileKind.Other"/> убирает расширение из таблицы.
    /// </summary>
    /// <param name="overrides">Расширения (с точкой или без, любой регистр) и их виды.</param>
    public FileKindMap With(IEnumerable<KeyValuePair<string, FileKind>> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);

        var builder = _byExtension.ToDictionary(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawExtension, kind) in overrides)
        {
            var extension = NormalizeExtension(rawExtension);
            if (extension.Length == 0)
            {
                continue;
            }

            if (kind is FileKind.Other or FileKind.Folder)
            {
                builder.Remove(extension);
            }
            else
            {
                builder[extension] = kind;
            }
        }

        return new FileKindMap(builder.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
    }

    private static FrozenDictionary<string, FileKind> BuildDefault()
    {
        var builder = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var (kind, extensions) in BuiltIn)
        {
            foreach (var extension in extensions)
            {
                builder.Add(extension, kind);
            }
        }

        return builder.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        var trimmed = extension.Trim();
        return trimmed[0] == '.' ? trimmed[1..].ToLowerInvariant() : trimmed.ToLowerInvariant();
    }
}

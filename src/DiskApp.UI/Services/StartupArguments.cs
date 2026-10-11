namespace DiskApp.UI.Services;

/// <summary>
/// Аргументы командной строки, понятные оболочке (PLAN.md, этап 6.4). Те же команды второй экземпляр
/// будет передавать первому через именованный канал.
/// </summary>
/// <param name="Minimized">Стартовать свёрнутым в трей, не показывая главное окно.</param>
/// <param name="Show">Показать главное окно.</param>
/// <param name="OpenSettings">Открыть страницу настроек.</param>
/// <param name="SearchQuery">Показать быстрый поиск с этим запросом (пустая строка — без запроса); <c>null</c> — не показывать.</param>
/// <param name="Portable">Портативный режим: данные рядом с исполняемым файлом.</param>
/// <param name="Exit">Завершить запущенный экземпляр штатно (скрипты, установщик); если он не запущен — ничего не делать.</param>
/// <param name="AddPaths">Папки, которые добавить корнями (<c>--add &lt;пути…&gt;</c>, «Отправить» в Проводнике); <c>null</c> — нет.</param>
public sealed record StartupArguments(
    bool Minimized,
    bool Show,
    bool OpenSettings,
    string? SearchQuery,
    bool Portable,
    bool Exit = false,
    IReadOnlyList<string>? AddPaths = null)
{
    /// <summary>Схема ссылок DiskApp: <c>diskapp://search?q=…</c>, <c>diskapp://open?path=…</c>.</summary>
    public const string UriScheme = "diskapp";
    /// <summary>Аргументы по умолчанию: обычный запуск с главным окном.</summary>
    public static StartupArguments Default { get; } = new(false, false, false, null, false);

    /// <summary>Показывать ли окно быстрого поиска.</summary>
    public bool ShowSearch => SearchQuery is not null;

    /// <summary>
    /// Разбирает аргументы. Неизвестные аргументы пропускаются; значение <c>--search</c> — следующий аргумент
    /// или часть после <c>=</c>.
    /// </summary>
    public static StartupArguments Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var minimized = false;
        var show = false;
        var settings = false;
        var portable = false;
        var exit = false;
        string? search = null;
        List<string>? add = null;

        for (var i = 0; i < args.Count; i++)
        {
            var argument = args[i];
            var separator = argument.IndexOf('=', StringComparison.Ordinal);
            var name = separator > 0 ? argument[..separator] : argument;
            var inlineValue = separator > 0 ? argument[(separator + 1)..] : null;

            switch (name.ToLowerInvariant())
            {
                case "--minimized":
                    minimized = true;
                    break;
                case "--show":
                    show = true;
                    break;
                case "--settings":
                    settings = true;
                    break;
                case "--portable":
                    portable = true;
                    break;
                case "--exit":
                    exit = true;
                    break;
                case "--add":
                    add ??= [];
                    if (inlineValue is not null)
                    {
                        add.Add(inlineValue);
                    }

                    while (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal) && !IsAppUri(args[i + 1]))
                    {
                        add.Add(args[++i]);
                    }

                    break;
                case "--search":
                    if (inlineValue is not null)
                    {
                        search = inlineValue;
                    }
                    else if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        search = args[++i];
                    }
                    else
                    {
                        search = string.Empty;
                    }

                    break;
                default:
                    if (IsAppUri(argument) && ParseUri(argument) is { } query)
                    {
                        search = query;
                    }

                    break;
            }
        }

        return new StartupArguments(minimized, show, settings, search, portable, exit, add is { Count: > 0 } ? add : null);
    }

    private static bool IsAppUri(string argument) => argument.StartsWith(UriScheme + ":", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Запрос быстрого поиска из ссылки: <c>search?q=</c> — сам запрос, <c>open?path=</c> — имя файла (открытие по пути
    /// появится с обзором, этап 8).
    /// </summary>
    private static string? ParseUri(string argument)
    {
        if (!Uri.TryCreate(argument, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var action = (uri.Host.Length > 0 ? uri.Host : uri.AbsolutePath.Trim('/')).ToLowerInvariant();
        var parameters = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(static pair => pair.Split('=', 2))
            .Where(static pair => pair.Length == 2)
            .GroupBy(static pair => pair[0].ToLowerInvariant())
            .ToDictionary(static group => group.Key, static group => Uri.UnescapeDataString(group.First()[1].Replace('+', ' ')), StringComparer.Ordinal);
        return action switch
        {
            "search" => parameters.GetValueOrDefault("q") ?? string.Empty,
            "open" when parameters.TryGetValue("path", out var path) => Path.GetFileName(path.TrimEnd('\\', '/')),
            _ => null,
        };
    }
}

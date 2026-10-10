using DiskApp.Core.Abstractions;

namespace DiskApp.Infrastructure;

/// <summary>
/// Папки и файлы приложения (docs/ARCHITECTURE.md, раздел 4): обычный режим — <c>%LocalAppData%\DiskApp</c>,
/// портативный — <c>data\</c> рядом с исполняемым файлом при файле <c>portable</c> рядом с exe или аргументе <c>--portable</c>.
/// <see cref="Resolve"/> создаёт папки журналов, кэша и резервных копий.
/// </summary>
public sealed class AppPaths : IAppPaths
{
    /// <summary>Имя папки продукта в <c>%LocalAppData%</c>.</summary>
    public const string ProductFolderName = "DiskApp";

    /// <summary>Файл-маркер портативного режима рядом с исполняемым файлом.</summary>
    public const string PortableMarkerFileName = "portable";

    /// <summary>Аргумент командной строки, включающий портативный режим.</summary>
    public const string PortableArgument = "--portable";

    private AppPaths(string dataDirectory, bool isPortable)
    {
        DataDirectory = dataDirectory;
        IsPortable = isPortable;
        DatabasePath = Path.Combine(dataDirectory, "index.db");
        SettingsPath = Path.Combine(dataDirectory, "settings.json");
        LogsDirectory = Path.Combine(dataDirectory, "logs");
        CacheDirectory = Path.Combine(dataDirectory, "cache");
        IconCacheDirectory = Path.Combine(CacheDirectory, "icons");
        ThumbnailCacheDirectory = Path.Combine(CacheDirectory, "thumbs");
        BackupsDirectory = Path.Combine(dataDirectory, "backups");
    }

    /// <inheritdoc />
    public bool IsPortable { get; }

    /// <inheritdoc />
    public string DataDirectory { get; }

    /// <inheritdoc />
    public string DatabasePath { get; }

    /// <inheritdoc />
    public string SettingsPath { get; }

    /// <inheritdoc />
    public string LogsDirectory { get; }

    /// <inheritdoc />
    public string CacheDirectory { get; }

    /// <inheritdoc />
    public string IconCacheDirectory { get; }

    /// <inheritdoc />
    public string ThumbnailCacheDirectory { get; }

    /// <inheritdoc />
    public string BackupsDirectory { get; }

    /// <summary>
    /// Определяет папки по аргументам и окружению и создаёт их.
    /// </summary>
    /// <param name="args">Аргументы командной строки.</param>
    /// <param name="baseDirectory">Папка исполняемого файла; по умолчанию <see cref="AppContext.BaseDirectory"/>.</param>
    /// <param name="localAppData">Папка <c>%LocalAppData%</c>; по умолчанию системная.</param>
    public static AppPaths Resolve(IReadOnlyList<string> args, string? baseDirectory = null, string? localAppData = null)
    {
        ArgumentNullException.ThrowIfNull(args);

        baseDirectory ??= AppContext.BaseDirectory;
        var isPortable = args.Contains(PortableArgument, StringComparer.OrdinalIgnoreCase)
            || File.Exists(Path.Combine(baseDirectory, PortableMarkerFileName));

        var dataDirectory = isPortable
            ? Path.Combine(baseDirectory, "data")
            : Path.Combine(localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolderName);

        return ForDataDirectory(Path.GetFullPath(dataDirectory), isPortable);
    }

    /// <summary>
    /// Пути внутри заданной папки данных (тесты, диагностика); создаёт папки.
    /// </summary>
    public static AppPaths ForDataDirectory(string dataDirectory, bool isPortable = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        var paths = new AppPaths(Path.GetFullPath(dataDirectory), isPortable);
        paths.EnsureDirectories();
        return paths;
    }

    /// <summary>
    /// Создаёт папки данных, журналов, кэша и резервных копий, если их нет.
    /// </summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(IconCacheDirectory);
        Directory.CreateDirectory(ThumbnailCacheDirectory);
        Directory.CreateDirectory(BackupsDirectory);
    }
}

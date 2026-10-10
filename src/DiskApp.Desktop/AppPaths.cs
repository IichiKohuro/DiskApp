namespace DiskApp.Desktop;

/// <summary>
/// Папки данных приложения: обычный режим — <c>%LocalAppData%\DiskApp</c>, портативный — <c>data\</c> рядом
/// с исполняемым файлом (файл <c>portable</c> рядом с exe или аргумент <c>--portable</c>).
/// </summary>
/// <param name="DataDirectory">Папка данных: база, настройки, кэш, журналы.</param>
/// <param name="LogsDirectory">Папка журналов.</param>
/// <param name="IsPortable">Включён ли портативный режим.</param>
internal sealed record AppPaths(string DataDirectory, string LogsDirectory, bool IsPortable)
{
    private const string ProductFolderName = "DiskApp";
    private const string PortableMarkerFileName = "portable";

    /// <summary>
    /// Определяет папки по аргументам и окружению и создаёт их.
    /// </summary>
    public static AppPaths Resolve(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var baseDirectory = AppContext.BaseDirectory;
        var isPortable = args.Contains("--portable", StringComparer.OrdinalIgnoreCase)
            || File.Exists(Path.Combine(baseDirectory, PortableMarkerFileName));

        var dataDirectory = isPortable
            ? Path.Combine(baseDirectory, "data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductFolderName);
        var logsDirectory = Path.Combine(dataDirectory, "logs");

        Directory.CreateDirectory(logsDirectory);
        return new AppPaths(dataDirectory, logsDirectory, isPortable);
    }
}

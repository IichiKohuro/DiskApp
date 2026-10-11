using System.Diagnostics;

using DiskApp.Client;
using DiskApp.Core.Abstractions;
using DiskApp.Core.Settings;
using DiskApp.Infrastructure.Diagnostics;
using DiskApp.UI.Services;

using Serilog;

namespace DiskApp.Desktop.Shell;

/// <summary>
/// Обслуживание данных (PLAN.md, этап 9.1, раздел «Данные»): папки и размеры, пересборка индекса через API, очистка кэша
/// иконок и миниатюр, список сторонних компонентов из ресурса, созданного при сборке.
/// </summary>
internal sealed class DesktopMaintenance(IAppPaths paths, IDiskAppApi api, ILogger log, IIconCache? icons = null, ISettingsStore<AppSettings>? settings = null) : IAppMaintenance
{
    private const string ThirdPartyResource = "DiskApp.ThirdParty.txt";

    // Хвост журнала читается с конца файла: больше мегабайта для страницы журнала не нужно.
    private const long LogTailBytes = 1024 * 1024;

    private readonly ILogger _log = log.ForContext<DesktopMaintenance>();
    private IReadOnlyList<ThirdPartyComponent>? _thirdParty;

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public string DataDirectory => paths.DataDirectory;

    /// <inheritdoc />
    public string LogsDirectory => paths.LogsDirectory;

    /// <inheritdoc />
    public long DatabaseSize => FileSize(paths.DatabasePath) + FileSize(paths.DatabasePath + "-wal") + FileSize(paths.DatabasePath + "-shm");

    /// <inheritdoc />
    public long CacheSize => DirectorySize(paths.CacheDirectory);

    /// <inheritdoc />
    public IReadOnlyList<ThirdPartyComponent> ThirdParty => _thirdParty ??= LoadThirdParty();

    /// <inheritdoc />
    public void OpenFolder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(path);
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add(path);
        using var _ = Process.Start(start);
    }

    /// <inheritdoc />
    public Task ClearCacheAsync() => Task.Run(() =>
    {
        foreach (var folder in new[] { paths.IconCacheDirectory, paths.ThumbnailCacheDirectory })
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Файл держит поток иконок: он перезапишется при следующем показе.
                    _log.Debug(exception, "Файл кэша не удалён");
                }
            }
        }

        icons?.Clear();
    });

    /// <inheritdoc />
    public Task RebuildIndexAsync() => api.RebuildIndexAsync();

    /// <inheritdoc />
    public async Task<string> CreateDiagnosticArchiveAsync()
    {
        var output = Path.Combine(paths.DataDirectory, "diagnostics", $"diskapp-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        var path = await DiagnosticArchive.CreateAsync(new DiagnosticArchiveRequest(output, paths.LogsDirectory, paths.SettingsPath, DescribeEnvironment())).ConfigureAwait(false);
        _log.Information("Диагностический архив собран: {Path}", path);
        return path;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ReadLogTailAsync(int maxLines) => Task.Run<IReadOnlyList<string>>(() =>
    {
        if (DiagnosticArchive.LatestLogs(paths.LogsDirectory, 1) is not [var file])
        {
            return [];
        }

        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var skipFirst = stream.Length > LogTailBytes;
        if (skipFirst)
        {
            stream.Seek(-LogTailBytes, SeekOrigin.End);
        }

        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        var lines = reader.ReadToEnd().Split('\n').Select(static line => line.TrimEnd('\r')).ToList();

        // С середины файла первая строка обрезана; пустой хвост после последнего перевода строки не нужен.
        if (skipFirst && lines.Count > 0)
        {
            lines.RemoveAt(0);
        }

        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines.Count > maxLines ? lines[^maxLines..] : lines;
    });

    /// <inheritdoc />
    public async Task<string?> ResetSettingsAsync()
    {
        if (settings is null)
        {
            throw new InvalidOperationException("Хранилище настроек недоступно");
        }

        string? backup = null;
        if (File.Exists(paths.SettingsPath))
        {
            backup = $"{paths.SettingsPath}.bak-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(paths.SettingsPath, backup, overwrite: true);
        }

        await settings.SaveAsync(new AppSettings()).ConfigureAwait(false);
        _log.Warning("Настройки сброшены к значениям по умолчанию; прежние сохранены в {Backup}", backup);
        return backup;
    }

    private Dictionary<string, string> DescribeEnvironment()
    {
        using var process = Process.GetCurrentProcess();
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Версия DiskApp"] = AppVersion.Current,
            ["Собран"] = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture),
            ["ОС"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ["Среда"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            ["Архитектура"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            ["Язык интерфейса"] = System.Globalization.CultureInfo.CurrentUICulture.Name,
            ["Язык Windows"] = System.Globalization.CultureInfo.InstalledUICulture.Name,
            ["Портативный режим"] = paths.IsPortable ? "да" : "нет",
            ["Папка данных"] = paths.DataDirectory,
            ["Размер базы"] = DatabaseSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Размер кэша"] = CacheSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Работает"] = (DateTime.Now - process.StartTime).ToString(@"d\.hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture),
            ["Память, МБ"] = (process.WorkingSet64 / (1024 * 1024)).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    private static long FileSize(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static long DirectorySize(string path)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }

        try
        {
            return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(static file => new FileInfo(file).Length);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static IReadOnlyList<ThirdPartyComponent> LoadThirdParty()
    {
        using var stream = typeof(DesktopMaintenance).Assembly.GetManifestResourceStream(ThirdPartyResource);
        if (stream is null)
        {
            return [];
        }

        using var reader = new StreamReader(stream);
        return [.. reader.ReadToEnd()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static line => line.Split(' ', 2, StringSplitOptions.TrimEntries))
            .Where(static parts => parts.Length == 2)
            .Select(static parts => new ThirdPartyComponent(parts[0], parts[1]))
            .OrderBy(static component => component.Name, StringComparer.OrdinalIgnoreCase)];
    }
}

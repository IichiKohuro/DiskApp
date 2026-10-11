using System.Diagnostics;

using DiskApp.Client;
using DiskApp.Core.Abstractions;
using DiskApp.UI.Services;

using Serilog;

namespace DiskApp.Desktop.Shell;

/// <summary>
/// Обслуживание данных (PLAN.md, этап 9.1, раздел «Данные»): папки и размеры, пересборка индекса через API, очистка кэша
/// иконок и миниатюр, список сторонних компонентов из ресурса, созданного при сборке.
/// </summary>
internal sealed class DesktopMaintenance(IAppPaths paths, IDiskAppApi api, ILogger log, IIconCache? icons = null) : IAppMaintenance
{
    private const string ThirdPartyResource = "DiskApp.ThirdParty.txt";

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

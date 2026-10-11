using System.Net.Http.Json;
using System.Text.Json.Serialization.Metadata;

using DiskApp.Contracts;
using DiskApp.Contracts.Roots;
using DiskApp.Core.Abstractions;
using DiskApp.Core.Indexing;
using DiskApp.Core.Results;
using DiskApp.Infrastructure;

using Microsoft.Extensions.DependencyInjection;

namespace DiskApp.Server.Tests.Api;

/// <summary>
/// Сервер на loopback с временными папками данных и файлов, подменной оболочкой и иконками; клиент HTTP к нему.
/// </summary>
internal sealed class ServerTestContext : IAsyncDisposable
{
    private ServerTestContext(TemporaryDirectory data, TemporaryDirectory files, DiskAppServerHost host, FakeShell? shell)
    {
        Data = data;
        Files = files;
        Host = host;
        Shell = shell;
        Client = new HttpClient { BaseAddress = host.BaseAddress, Timeout = TimeSpan.FromSeconds(30) };
    }

    public TemporaryDirectory Data { get; }

    public TemporaryDirectory Files { get; }

    public DiskAppServerHost Host { get; }

    public FakeShell? Shell { get; }

    public HttpClient Client { get; }

    public IndexerService Indexer => Host.Services.GetRequiredService<IndexerService>();

    public static Task<ServerTestContext> StartAsync(bool withPlatform = true, string environment = "Production")
        => StartAsync(withPlatform, environment, configureServices: null);

    public static async Task<ServerTestContext> StartAsync(bool withPlatform, string environment, Action<IServiceCollection>? configureServices)
    {
        var data = new TemporaryDirectory("api-data");
        var files = new TemporaryDirectory("api-files");
        var shell = withPlatform ? new FakeShell(data.CreateDirectory("_trash")) : null;
        var host = DiskAppServerHost.Create(
            new DiskAppServerOptions { Paths = AppPaths.ForDataDirectory(data.FullPath), EnvironmentName = environment },
            services =>
            {
                if (shell is not null)
                {
                    services.AddSingleton<IPlatformShell>(shell);
                    services.AddSingleton<IIconProvider, FakeIcons>();
                }

                configureServices?.Invoke(services);
            });
        await host.StartAsync(TestContext.Current.CancellationToken);
        return new ServerTestContext(data, files, host, shell);
    }

    /// <summary>Добавляет корень через API и ждёт конца первого сканирования.</summary>
    public async Task<RootDto> AddRootAsync(string? relativeFolder = null)
    {
        var path = relativeFolder is null ? Files.FullPath : Files.Resolve(relativeFolder);
        Directory.CreateDirectory(path);
        var response = await Client.PostAsJsonAsync(ApiRoutes.V1.Roots, new CreateRootRequest { Path = path }, DiskAppJsonContext.Default.CreateRootRequest, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var root = (await response.Content.ReadFromJsonAsync(DiskAppJsonContext.Default.RootDto, TestContext.Current.CancellationToken))!;
        await WaitIdleAsync();
        return root;
    }

    public Task WaitIdleAsync() => Indexer.WhenIdleAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

    public async Task<T> GetAsync<T>(string uri, JsonTypeInfo<T> type)
    {
        using var response = await Client.GetAsync(new Uri(uri, UriKind.Relative), TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(type, TestContext.Current.CancellationToken))!;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Host.StopAsync(TestContext.Current.CancellationToken);
        await Host.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Files.Dispose();
        Data.Dispose();
    }
}

/// <summary>
/// Оболочка ОС для тестов: запоминает вызовы, ничего не запускает; «корзина» переносит файлы во временную папку.
/// Может вернуть ошибку.
/// </summary>
internal sealed class FakeShell(string trash) : IPlatformShell
{
    private readonly List<(string Action, string Path)> _calls = [];

    public DomainError? FailWith { get; set; }

    public IReadOnlyList<(string Action, string Path)> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    public Task<Result> OpenAsync(string path, CancellationToken cancellationToken = default) => Record("open", path);

    public Task<Result> OpenWithAsync(string path, CancellationToken cancellationToken = default) => Record("open-with", path);

    public Task<Result> RevealInExplorerAsync(string path, CancellationToken cancellationToken = default) => Record("reveal", path);

    public Task<Result> ShowPropertiesAsync(string path, CancellationToken cancellationToken = default) => Record("properties", path);

    public Task<Result> MoveToRecycleBinAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        if (FailWith is null)
        {
            foreach (var path in paths)
            {
                var target = Path.Combine(trash, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(path));
                if (Directory.Exists(path))
                {
                    Directory.Move(path, target);
                }
                else if (File.Exists(path))
                {
                    File.Move(path, target);
                }
            }
        }

        return Record("recycle", string.Join('|', paths));
    }

    public Task<Result> RunAsAdminAsync(string path, CancellationToken cancellationToken = default) => Record("run-as-admin", path);

    public Task<Result> CopyPathsToClipboardAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) => Record("copy-paths", string.Join('|', paths));

    public Task<Result> CopyFilesToClipboardAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) => Record("copy-files", string.Join('|', paths));

    private Task<Result> Record(string action, string path)
    {
        lock (_calls)
        {
            _calls.Add((action, path));
        }

        return Task.FromResult(FailWith is { } error ? Result.Fail(error) : Result.Ok());
    }
}

/// <summary>
/// Иконки для тестов: PNG 1×1 с отметкой источника в размере.
/// </summary>
internal sealed class FakeIcons : IIconProvider
{
    public static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    public Task<RasterImage?> GetIconForExtensionAsync(string extension, int size, CancellationToken cancellationToken = default)
        => Task.FromResult<RasterImage?>(new RasterImage(Png, size, size));

    public Task<RasterImage?> GetIconForFileAsync(string path, int size, CancellationToken cancellationToken = default)
        => Task.FromResult<RasterImage?>(new RasterImage(Png, size, size));

    public Task<RasterImage?> GetThumbnailAsync(string path, int size, CancellationToken cancellationToken = default)
        => Task.FromResult<RasterImage?>(new RasterImage(Png, size, size));
}

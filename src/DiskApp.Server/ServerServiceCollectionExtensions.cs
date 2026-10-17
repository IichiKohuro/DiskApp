using DiskApp.Contracts;
using DiskApp.Core.Abstractions;
using DiskApp.Core.Domain;
using DiskApp.Core.Events;
using DiskApp.Core.FileSystem;
using DiskApp.Core.History;
using DiskApp.Core.Indexing;
using DiskApp.Core.Indexing.Watching;
using DiskApp.Core.Operations;
using DiskApp.Core.Paths;
using DiskApp.Core.Repositories;
using DiskApp.Core.Search;
using DiskApp.Core.Settings;
using DiskApp.Core.Sorting;
using DiskApp.Infrastructure.FileSystem;
using DiskApp.Infrastructure.Settings;
using DiskApp.Infrastructure.Sqlite;
using DiskApp.Infrastructure.Sqlite.Repositories;
using DiskApp.Infrastructure.Sqlite.Search;
using DiskApp.Server.Endpoints;
using DiskApp.Server.Hosting;
using DiskApp.Server.Realtime;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DiskApp.Server;

/// <summary>
/// Регистрация сервера в общем DI-контейнере (PLAN.md, этап 4.1): сервисы Core и Infrastructure, запуск базы
/// и индексатора, JSON-контекст API, ProblemDetails. Десктоп добавляет свои сервисы в тот же контейнер.
/// </summary>
public static class ServerServiceCollectionExtensions
{
    /// <summary>
    /// Добавляет сервисы сервера.
    /// </summary>
    public static IServiceCollection AddDiskAppServer(this IServiceCollection services, DiskAppServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.AddSingleton(options.Paths);
        services.AddSingleton<ServerState>();
        services.AddSingleton<IClock>(SystemClock.Instance);
        services.AddSingleton(PathNormalizer.Current);
        services.AddSingleton(FileKindMap.Default);

        // Настройки читаются при первом обращении; повреждённый файл откладывается, настройки берутся из резервной копии или по умолчанию.
        services.AddSingleton(static provider =>
        {
            var paths = provider.GetRequiredService<IAppPaths>();
            var store = AppSettingsStore.Create(paths.SettingsPath);
            var result = store.Load();
            provider.GetRequiredService<ServerState>().SetSettings(result);
            if (result.Outcome is SettingsLoadOutcome.Corrupt or SettingsLoadOutcome.RestoredFromBackup)
            {
                ServerLog.SettingsRecovered(provider.GetRequiredService<ILogger<ServerState>>(), paths.SettingsPath, result.Outcome.ToString(), result.CorruptFilePath);
            }

            return store;
        });
        services.AddSingleton<ISettingsStore<AppSettings>>(static provider => provider.GetRequiredService<JsonSettingsStore<AppSettings>>());

        AddStorage(services, options);
        AddIndexing(services);
        AddSearch(services);

        services.AddSingleton<EntryLookup>();
        services.AddSingleton<RootGuard>();
        services.AddSingleton<IPathChangeSink>(static provider => provider.GetRequiredService<IndexerService>());
        services.AddSingleton(new FileOperationOptions());
        services.AddSingleton(static provider => new FileOperationService(
            new System.IO.Abstractions.FileSystem(),
            provider.GetRequiredService<IIndexEventBus>(),
            provider.GetRequiredService<IClock>(),
            provider.GetRequiredService<PathNormalizer>(),
            provider.GetRequiredService<FileOperationOptions>(),
            provider.GetService<IPlatformShell>(),
            provider.GetRequiredService<IPathChangeSink>(),
            provider.GetRequiredService<ILogger<FileOperationService>>(),
            provider.GetService<IFileLockInspector>()));
        services.AddSingleton(static provider => new SortingHistory(
            provider.GetRequiredService<ISortingStatsRepository>(),
            provider.GetRequiredService<ISettingsStore<AppSettings>>(),
            provider.GetRequiredService<IClock>(),
            provider.GetRequiredService<PathNormalizer>(),
            new System.IO.Abstractions.FileSystem()));
        services.AddSingleton(static provider => new SortingService(
            provider.GetRequiredService<FileOperationService>(),
            new System.IO.Abstractions.FileSystem(),
            provider.GetRequiredService<IIndexEventBus>(),
            provider.GetRequiredService<IRootRepository>(),
            provider.GetRequiredService<IFileIndexRepository>(),
            provider.GetRequiredService<ITagRepository>(),
            provider.GetRequiredService<PathNormalizer>(),
            provider.GetRequiredService<ILogger<SortingService>>(),
            provider.GetRequiredService<SortingHistory>()));
        services.AddSingleton(static provider => new SortingFolderTracker(
            provider.GetRequiredService<IIndexEventBus>(),
            provider.GetRequiredService<FileOperationService>(),
            provider.GetRequiredService<ISettingsStore<AppSettings>>(),
            provider.GetRequiredService<PathNormalizer>(),
            provider.GetRequiredService<ILogger<SortingFolderTracker>>(),
            provider.GetRequiredService<IFolderTagRepository>(),
            () => provider.GetRequiredService<SearchService>().InvalidateCaches(),
            provider.GetRequiredService<SortingHistory>(),
            provider.GetRequiredService<IProjectRepository>()));
        services.AddSingleton(static provider => new InboxService(
            provider.GetRequiredService<ISettingsStore<AppSettings>>(),
            provider.GetRequiredService<IRootRepository>(),
            provider.GetRequiredService<IFileIndexRepository>(),
            provider.GetRequiredService<IInboxStateRepository>(),
            provider.GetRequiredService<IClock>(),
            provider.GetRequiredService<PathNormalizer>(),
            new System.IO.Abstractions.FileSystem()));
        services.AddSingleton(static provider => new AutoSortService(
            provider.GetRequiredService<InboxService>(),
            provider.GetRequiredService<SortingService>(),
            provider.GetRequiredService<FileOperationService>(),
            provider.GetRequiredService<ISettingsStore<AppSettings>>(),
            provider.GetRequiredService<IInboxStateRepository>(),
            provider.GetRequiredService<IRootRepository>(),
            provider.GetRequiredService<IIndexEventBus>(),
            provider.GetRequiredService<IClock>(),
            provider.GetRequiredService<PathNormalizer>(),
            new System.IO.Abstractions.FileSystem(),
            provider.GetRequiredService<ILogger<AutoSortService>>()));
        services.AddSingleton(static provider => new FileHistoryService(
            provider.GetRequiredService<IFileHistoryRepository>(),
            provider.GetRequiredService<IIndexEventBus>(),
            provider.GetRequiredService<FileOperationService>(),
            provider.GetRequiredService<ISettingsStore<AppSettings>>(),
            provider.GetRequiredService<IClock>(),
            provider.GetRequiredService<PathNormalizer>(),
            new System.IO.Abstractions.FileSystem(),
            provider.GetService<IRecycleBin>(),
            provider.GetRequiredService<ILogger<FileHistoryService>>()));
        services.AddHostedService<SortingFolderTrackerService>();

        // История файлов подписывается раньше, чем стартует индексатор, и дописывает накопленное после его остановки.
        services.AddHostedService<FileHistoryHostedService>();
        services.AddHostedService<ServerStartupService>();
        services.AddHostedService<SearchCacheInvalidator>();
        services.AddHostedService(static provider => provider.GetRequiredService<AutoSortService>());
        services.AddProblemDetails();
        services.ConfigureHttpJsonOptions(static json => json.SerializerOptions.TypeInfoResolverChain.Insert(0, DiskAppJsonContext.Default));
        AddRealtime(services);
        return services;
    }

    private static void AddRealtime(IServiceCollection services)
    {
        // Таймауты под локальную сеть: обрыв замечается за полминуты, keep-alive не нагружает простой.
        services.AddSignalR(static hub =>
            {
                hub.KeepAliveInterval = TimeSpan.FromSeconds(10);
                hub.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
                hub.HandshakeTimeout = TimeSpan.FromSeconds(15);
                hub.MaximumReceiveMessageSize = 64 * 1024;
            })
            .AddJsonProtocol(static json => json.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, DiskAppJsonContext.Default));
        services.AddSingleton(new HubBroadcastOptions());
        services.AddSingleton<HubBroadcaster>();
        services.AddHostedService(static provider => provider.GetRequiredService<HubBroadcaster>());
    }

    private static void AddStorage(IServiceCollection services, DiskAppServerOptions options)
    {
        services.AddSingleton(_ => new SqliteDatabase(options.Paths.DatabasePath));
        services.AddSingleton<IRootRepository>(static provider => new SqliteRootRepository(provider.GetRequiredService<SqliteDatabase>(), provider.GetRequiredService<IClock>()));
        services.AddSingleton<IFileIndexRepository>(static provider => new SqliteFileIndexRepository(
            provider.GetRequiredService<SqliteDatabase>(), provider.GetRequiredService<PathNormalizer>(), provider.GetRequiredService<FileKindMap>()));
        services.AddSingleton<IScanLogRepository>(static provider => new SqliteScanLogRepository(provider.GetRequiredService<SqliteDatabase>()));
        services.AddSingleton<IUsageRepository>(static provider => new SqliteUsageRepository(provider.GetRequiredService<SqliteDatabase>()));
        services.AddSingleton<IFavoriteRepository>(static provider => new SqliteFavoriteRepository(provider.GetRequiredService<SqliteDatabase>()));
        services.AddSingleton<ITagRepository>(static provider => new SqliteTagRepository(provider.GetRequiredService<SqliteDatabase>()));
        services.AddSingleton<IFolderTagRepository>(static provider => new SqliteFolderTagRepository(provider.GetRequiredService<SqliteDatabase>(), provider.GetRequiredService<PathNormalizer>()));
        services.AddSingleton<IInboxStateRepository>(static provider => new SqliteInboxStateRepository(provider.GetRequiredService<SqliteDatabase>(), provider.GetRequiredService<PathNormalizer>()));
        services.AddSingleton<ISortingStatsRepository>(static provider => new SqliteSortingStatsRepository(provider.GetRequiredService<SqliteDatabase>()));
        services.AddSingleton<IFileHistoryRepository>(static provider => new SqliteFileHistoryRepository(provider.GetRequiredService<SqliteDatabase>(), provider.GetRequiredService<PathNormalizer>()));
        services.AddSingleton<INoteRepository>(static provider => new SqliteNoteRepository(provider.GetRequiredService<SqliteDatabase>()));
        services.AddSingleton<IProjectRepository>(static provider => new SqliteProjectRepository(provider.GetRequiredService<SqliteDatabase>()));
        services.AddSingleton<INoteTagRepository>(static provider => new SqliteNoteTagRepository(provider.GetRequiredService<SqliteDatabase>()));
        services.AddSingleton<ICollectionRepository>(static provider => new SqliteCollectionRepository(provider.GetRequiredService<SqliteDatabase>(), provider.GetRequiredService<IClock>()));
        services.AddSingleton<IQueryHistoryRepository>(static provider => new SqliteQueryHistoryRepository(provider.GetRequiredService<SqliteDatabase>()));
    }

    private static void AddIndexing(IServiceCollection services)
    {
        services.AddSingleton<IIndexEventBus, IndexEventBus>();
        services.AddSingleton<IFileSystemLister>(FileSystemLister.Instance);
        services.AddSingleton<IRootWatcherFactory>(static provider => new FileSystemRootWatcherFactory(provider.GetRequiredService<PathNormalizer>()));
        services.AddSingleton<IDriveInfoProvider, DriveInfoProvider>();
        services.AddSingleton<IBackgroundPriority>(ThreadBackgroundPriority.Instance);
        services.AddSingleton(static provider => new EntryFactory(provider.GetRequiredService<PathNormalizer>(), provider.GetRequiredService<FileKindMap>()));

        // Индексатор создаётся после открытия базы: восстановленный после повреждения индекс сканируется заново целиком.
        services.AddSingleton(static provider =>
        {
            var database = provider.GetRequiredService<ServerState>().Database;
            var recovered = database.IsCompletedSuccessfully && database.GetAwaiter().GetResult().WasRecovered;
            var settings = provider.GetRequiredService<ISettingsStore<AppSettings>>().Current;
            return new IndexerService(
                provider.GetRequiredService<IRootRepository>(),
                provider.GetRequiredService<IFileIndexRepository>(),
                provider.GetRequiredService<IScanLogRepository>(),
                provider.GetRequiredService<IFileSystemLister>(),
                provider.GetRequiredService<IRootWatcherFactory>(),
                provider.GetRequiredService<IDriveInfoProvider>(),
                provider.GetRequiredService<IIndexEventBus>(),
                provider.GetRequiredService<IClock>(),
                provider.GetRequiredService<EntryFactory>(),
                IndexerOptions.FromSettings(settings.Indexing, forceFullScan: recovered),
                provider.GetRequiredService<IBackgroundPriority>(),
                provider.GetRequiredService<ILogger<IndexerService>>());
        });
    }

    private static void AddSearch(IServiceCollection services)
    {
        services.AddSingleton<ISearchIndex>(static provider => new SqliteSearchIndex(provider.GetRequiredService<SqliteDatabase>(), provider.GetRequiredService<PathNormalizer>()));
        services.AddSingleton(static provider => new SearchService(
            provider.GetRequiredService<ISearchIndex>(),
            provider.GetRequiredService<IRootRepository>(),
            provider.GetRequiredService<IQueryHistoryRepository>(),
            provider.GetRequiredService<ITagRepository>(),
            provider.GetRequiredService<IClock>(),
            provider.GetRequiredService<PathNormalizer>(),
            SearchOptions.FromSettings(provider.GetRequiredService<ISettingsStore<AppSettings>>().Current.Search)));
    }
}

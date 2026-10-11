using DiskApp.Core.Domain;
using DiskApp.Core.Paths;
using DiskApp.Core.Search;
using DiskApp.Infrastructure.Sqlite;
using DiskApp.Infrastructure.Sqlite.Repositories;
using DiskApp.Infrastructure.Sqlite.Search;
using DiskApp.Infrastructure.Tests.Sqlite.Repositories;

namespace DiskApp.Infrastructure.Tests.Search;

/// <summary>
/// База с корнями и записями для тестов поиска: записи задаются путями, у каждой — размер, даты и атрибуты;
/// статистика, избранное и теги — через репозитории. Пути пишутся через <c>/</c>.
/// </summary>
internal sealed class SearchTestContext : IAsyncDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly Dictionary<long, long> _scanIds = [];

    private SearchTestContext(TemporaryDirectory folder, SqliteDatabase database, Root projects, Root photos)
    {
        Folder = folder;
        Database = database;
        Projects = projects;
        Photos = photos;
        Entries = new SqliteFileIndexRepository(database);
        Roots = new SqliteRootRepository(database, Clock);
        Usage = new SqliteUsageRepository(database);
        Favorites = new SqliteFavoriteRepository(database);
        Tags = new SqliteTagRepository(database);
        History = new SqliteQueryHistoryRepository(database);
        Index = new SqliteSearchIndex(database);
        FolderTags = new SqliteFolderTagRepository(database, PathNormalizer.Current);
    }

    public TemporaryDirectory Folder { get; }

    public SqliteDatabase Database { get; }

    public Root Projects { get; }

    public Root Photos { get; }

    public TestClock Clock { get; } = new(Now);

    public SqliteFileIndexRepository Entries { get; }

    public SqliteRootRepository Roots { get; }

    public SqliteUsageRepository Usage { get; }

    public SqliteFavoriteRepository Favorites { get; }

    public SqliteTagRepository Tags { get; }

    public SqliteQueryHistoryRepository History { get; }

    public SqliteSearchIndex Index { get; }

    public SqliteFolderTagRepository FolderTags { get; }

    public static async Task<SearchTestContext> CreateAsync()
    {
        var folder = new TemporaryDirectory("search");
        var database = new SqliteDatabase(folder.Resolve("index.db"));
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        var roots = new SqliteRootRepository(database, new TestClock(Now));
        var projects = await roots.AddAsync(new Root { Path = Root(@"D:\Projects", "/projects"), Label = "Проекты" }, TestContext.Current.CancellationToken);
        var photos = await roots.AddAsync(new Root { Path = Root(@"E:\Photos", "/photos"), Label = "Photos" }, TestContext.Current.CancellationToken);
        return new SearchTestContext(folder, database, projects, photos);
    }

    public SearchService Service(SearchOptions? options = null, ISearchIndex? index = null)
        => new(index ?? Index, Roots, History, Tags, Clock, PathNormalizer.Current, options ?? new SearchOptions(), TimeZoneInfo.Utc);

    /// <summary>
    /// Добавляет записи корня. Папки на пути создаются автоматически.
    /// </summary>
    public async Task AddAsync(Root root, params TestFile[] files)
    {
        var entries = new List<FileEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var parts = file.Path.Split('/');
            for (var i = 1; i < parts.Length; i++)
            {
                var folder = string.Join(PathNormalizer.Current.Separator, parts[..i]);
                if (seen.Add(folder) && await Entries.GetByPathAsync(root.Id, folder, TestContext.Current.CancellationToken) is null)
                {
                    entries.Add(FileEntry.Create(root.Id, null, folder, true, 0, Now.AddYears(-1), Now.AddYears(-1), FileAttributes.Directory));
                }
            }

            var relPath = string.Join(PathNormalizer.Current.Separator, parts);
            if (seen.Add(relPath))
            {
                entries.Add(FileEntry.Create(
                    root.Id,
                    null,
                    relPath,
                    file.IsDirectory,
                    file.Size,
                    file.Created ?? Now.AddYears(-1),
                    file.Modified ?? Now.AddDays(-100),
                    file.Attributes | (file.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal)));
            }
        }

        _scanIds[root.Id] = _scanIds.GetValueOrDefault(root.Id) + 1;
        foreach (var batch in entries.Chunk(2_000))
        {
            await Entries.UpsertAsync(batch, _scanIds[root.Id], TestContext.Current.CancellationToken);
        }
    }

    public async Task<FileEntry> GetAsync(Root root, string path)
        => await Entries.GetByPathAsync(root.Id, path.Replace('/', PathNormalizer.Current.Separator), TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException($"Нет записи {path}");

    public async Task OpenAsync(Root root, string path, int times, DateTimeOffset? at = null)
    {
        var entry = await GetAsync(root, path);
        for (var i = 0; i < times; i++)
        {
            await Usage.RecordOpenAsync(entry.Id, at ?? Now.AddHours(-1), TestContext.Current.CancellationToken);
        }
    }

    public async Task FavoriteAsync(Root root, string path)
        => await Favorites.AddAsync((await GetAsync(root, path)).Id, Now, TestContext.Current.CancellationToken);

    public async Task TagAsync(Root root, string path, string tag)
    {
        var created = await Tags.AddAsync(tag, null, TestContext.Current.CancellationToken);
        await Tags.AssignAsync((await GetAsync(root, path)).Id, created.Id, TestContext.Current.CancellationToken);
    }

    /// <summary>Ставит папке корня авто-теги (этап 19); пустой относительный путь — сам корень.</summary>
    public async Task AutoTagAsync(Root root, string folder, params string[] tags)
    {
        var ids = new List<long>();
        foreach (var tag in tags)
        {
            ids.Add((await Tags.AddAsync(tag, null, TestContext.Current.CancellationToken)).Id);
        }

        var path = folder.Length == 0 ? root.Path : Path.Combine(root.Path, folder.Replace('/', PathNormalizer.Current.Separator));
        await FolderTags.SetAsync(path, ids, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await Database.DisposeAsync();
        Folder.Dispose();
    }

    private static string Root(string windows, string posix) => OperatingSystem.IsWindows() ? windows : posix;
}

/// <summary>
/// Запись для тестовой базы.
/// </summary>
internal sealed record TestFile(string Path, long Size = 100, DateTimeOffset? Modified = null, DateTimeOffset? Created = null, FileAttributes Attributes = 0, bool IsDirectory = false)
{
    public static implicit operator TestFile(string path) => new(path);
}

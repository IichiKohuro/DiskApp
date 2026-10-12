using DiskApp.Core.Abstractions;
using DiskApp.Core.Domain;
using DiskApp.Core.Paths;
using DiskApp.Infrastructure.Sqlite;
using DiskApp.Infrastructure.Sqlite.Repositories;

namespace DiskApp.Infrastructure.Tests.Sqlite.Repositories;

/// <summary>
/// Готовая база для тестов репозиториев: временная папка, инициализированная <see cref="SqliteDatabase"/>,
/// один корень и все репозитории с фиксированными часами.
/// </summary>
internal sealed class IndexTestContext : IAsyncDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private IndexTestContext(TemporaryDirectory folder, SqliteDatabase database, Root root)
    {
        Folder = folder;
        Database = database;
        Root = root;
        Entries = new SqliteFileIndexRepository(database);
        Roots = new SqliteRootRepository(database, Clock);
        Usage = new SqliteUsageRepository(database);
        Favorites = new SqliteFavoriteRepository(database);
        Tags = new SqliteTagRepository(database);
        Collections = new SqliteCollectionRepository(database, Clock);
        History = new SqliteQueryHistoryRepository(database);
        FolderTags = new SqliteFolderTagRepository(database, PathNormalizer.Current);
        Notes = new SqliteNoteRepository(database);
        NoteTags = new SqliteNoteTagRepository(database);
    }

    public TemporaryDirectory Folder { get; }

    public SqliteDatabase Database { get; }

    public Root Root { get; }

    public TestClock Clock { get; } = new(Now);

    public SqliteFileIndexRepository Entries { get; }

    public SqliteRootRepository Roots { get; }

    public SqliteUsageRepository Usage { get; }

    public SqliteFavoriteRepository Favorites { get; }

    public SqliteTagRepository Tags { get; }

    public SqliteCollectionRepository Collections { get; }

    public SqliteQueryHistoryRepository History { get; }

    public SqliteFolderTagRepository FolderTags { get; }

    public SqliteNoteRepository Notes { get; }

    public SqliteNoteTagRepository NoteTags { get; }

    /// <summary>
    /// Создаёт базу и корень <c>D:\Projects</c> (или <c>/projects</c> вне Windows).
    /// </summary>
    public static async Task<IndexTestContext> CreateAsync()
    {
        var folder = new TemporaryDirectory("repo");
        var database = new SqliteDatabase(folder.Resolve("index.db"));
        try
        {
            await database.InitializeAsync(TestContext.Current.CancellationToken);
            var clock = new TestClock(Now);
            var roots = new SqliteRootRepository(database, clock);
            var rootPath = OperatingSystem.IsWindows() ? @"D:\Projects" : "/projects";
            var root = await roots.AddAsync(new Root { Path = rootPath, Label = "Projects" }, TestContext.Current.CancellationToken);
            return new IndexTestContext(folder, database, root);
        }
        catch
        {
            await database.DisposeAsync();
            folder.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Путь с разделителем текущей платформы: в тестах пути пишутся через <c>/</c>.
    /// </summary>
    public static string P(string path) => path.Replace('/', PathNormalizer.Current.Separator);

    /// <summary>
    /// Запись корня по относительному пути; размер и даты заданы, чтобы сравнение «не изменилась» было детерминированным.
    /// </summary>
    public FileEntry Entry(string relPath, bool isDirectory = false, long size = 100, DateTimeOffset? modified = null)
        => FileEntry.Create(
            Root.Id,
            parentId: null,
            relPath: P(relPath),
            isDirectory,
            size,
            createdUtc: Now.AddDays(-10),
            modifiedUtc: modified ?? Now.AddDays(-1),
            attributes: isDirectory ? FileAttributes.Directory : FileAttributes.Normal);

    /// <summary>
    /// Записывает дерево пакетом и возвращает итог.
    /// </summary>
    public Task<Core.Repositories.UpsertResult> UpsertAsync(long scanId, params FileEntry[] entries)
        => Entries.UpsertAsync(entries, scanId, TestContext.Current.CancellationToken);

    /// <summary>
    /// Запись по относительному пути или исключение, если её нет.
    /// </summary>
    public async Task<FileEntry> GetAsync(string relPath)
        => await Entries.GetByPathAsync(Root.Id, P(relPath), TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException($"Записи «{relPath}» нет в индексе.");

    public async ValueTask DisposeAsync()
    {
        await Database.DisposeAsync();
        Folder.Dispose();
    }
}

/// <summary>
/// Часы с заданным временем для детерминированных дат в базе.
/// </summary>
internal sealed class TestClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

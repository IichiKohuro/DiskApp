using Dapper;

using DiskApp.Core.Domain;
using DiskApp.Core.Events;
using DiskApp.Core.Indexing;
using DiskApp.Core.Paths;
using DiskApp.Infrastructure.FileSystem;
using DiskApp.Infrastructure.Sqlite;
using DiskApp.Infrastructure.Sqlite.Repositories;
using DiskApp.Infrastructure.Tests.Sqlite.Repositories;

namespace DiskApp.Infrastructure.Tests.Indexing;

/// <summary>
/// Папка с файлами, база индекса и всё для сканирования: сканер, конвейер, сверка, шина событий с записью событий.
/// Проверка «индекс совпадает с диском» сравнивает записи с перечислением файловой системы.
/// </summary>
internal sealed class IndexingTestContext : IAsyncDisposable
{
    private IndexingTestContext(TemporaryDirectory files, TemporaryDirectory data, SqliteDatabase database, Root root)
    {
        Files = files;
        Data = data;
        Database = database;
        Root = root;
        Entries = new SqliteFileIndexRepository(database);
        Roots = new SqliteRootRepository(database, Clock);
        ScanLog = new SqliteScanLogRepository(database);
        Factory = new EntryFactory(PathNormalizer.Current, FileKindMap.Default);
        Events.OnHandlerError = static (exception, _) => throw new InvalidOperationException("Ошибка обработчика события", exception);
        Events.Subscribe<IndexProgressEvent>(Progress.Add);
        Events.Subscribe<EntriesChangedEvent>(Changes.Add);
        Scanner = new DirectoryScanner(FileSystemLister.Instance, Factory, ThreadBackgroundPriority.Instance);
        Pipeline = new ScanPipeline(Scanner, Entries, ScanLog, Events, Clock, Factory);
        Reconcile = new ReconcileRunner(FileSystemLister.Instance, Entries, ScanLog, Events, Clock, Factory);
    }

    public TemporaryDirectory Files { get; }

    public TemporaryDirectory Data { get; }

    public SqliteDatabase Database { get; }

    public Root Root { get; private set; }

    public TestClock Clock { get; } = new(IndexTestContext.Now);

    public IndexEventBus Events { get; } = new();

    public List<IndexProgressEvent> Progress { get; } = [];

    public List<EntriesChangedEvent> Changes { get; } = [];

    public SqliteFileIndexRepository Entries { get; }

    public SqliteRootRepository Roots { get; }

    public SqliteScanLogRepository ScanLog { get; }

    public EntryFactory Factory { get; }

    public DirectoryScanner Scanner { get; }

    public ScanPipeline Pipeline { get; }

    public ReconcileRunner Reconcile { get; }

    public static async Task<IndexingTestContext> CreateAsync()
    {
        var files = new TemporaryDirectory("files");
        var data = new TemporaryDirectory("data");
        var database = new SqliteDatabase(data.Resolve("index.db"));
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        var roots = new SqliteRootRepository(database, new TestClock(IndexTestContext.Now));
        var root = await roots.AddAsync(new Root { Path = files.FullPath, Label = "files" }, TestContext.Current.CancellationToken);
        return new IndexingTestContext(files, data, database, root);
    }

    public ScanRequest Request(string relPath = "", IEnumerable<ExclusionRule>? rules = null, int parallelism = 2) => new()
    {
        Root = Root,
        RelPath = relPath,
        Matcher = ExclusionMatcher.Create(rules ?? [], Root, PathNormalizer.Current),
        DegreeOfParallelism = parallelism,
        BatchSize = 50,
    };

    public Task<ScanOutcome> FullScanAsync(string relPath = "", IEnumerable<ExclusionRule>? rules = null)
        => Pipeline.RunAsync(Request(relPath, rules), PauseToken.None, null, TestContext.Current.CancellationToken);

    public Task<ScanOutcome> ReconcileAsync(string relPath = "", IEnumerable<ExclusionRule>? rules = null)
        => Reconcile.RunAsync(Request(relPath, rules), PauseToken.None, TestContext.Current.CancellationToken);

    /// <summary>
    /// Записи индекса: путь → (папка, размер, время изменения).
    /// </summary>
    public Dictionary<string, (bool IsDirectory, long Size, string Modified)> IndexSnapshot()
    {
        using var connection = Database.Connections.Open();
        return connection
            .Query<(string RelPath, long IsDir, long Size, string Modified)>(
                "SELECT rel_path, is_dir, size, modified_utc FROM entries WHERE root_id = @RootId",
                new { RootId = Root.Id })
            .ToDictionary(static row => row.RelPath, static row => (row.IsDir != 0, row.Size, row.Modified), StringComparer.Ordinal);
    }

    /// <summary>
    /// Что лежит на диске: путь → (папка, размер, время изменения); внутрь ссылок не заходит.
    /// </summary>
    public Dictionary<string, (bool IsDirectory, long Size, string Modified)> DiskSnapshot()
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true };
        var result = new Dictionary<string, (bool, long, string)>(StringComparer.Ordinal);
        foreach (var info in new DirectoryInfo(Files.FullPath).EnumerateFileSystemInfos("*", options))
        {
            var relPath = Path.GetRelativePath(Files.FullPath, info.FullName);
            var isDirectory = info is DirectoryInfo;
            if (isDirectory)
            {
                // Перечисление заполняет сведения из записи родителя, которую NTFS обновляет лениво; берём из самой папки.
                info.Refresh();
            }

            var size = info is FileInfo file ? file.Length : 0;
            result[relPath] = (isDirectory, size, SqliteTime.Format(new DateTimeOffset(info.LastWriteTimeUtc)));
        }

        return result;
    }

    /// <summary>
    /// Индекс совпадает с диском, и у каждой записи правильный родитель.
    /// </summary>
    public void AssertIndexMatchesDisk()
    {
        var index = IndexSnapshot();
        var disk = DiskSnapshot();
        index.Keys.ShouldBe(disk.Keys, ignoreOrder: true);
        foreach (var (relPath, expected) in disk)
        {
            index[relPath].ShouldBe(expected, $"запись «{relPath}» расходится с диском");
        }

        AssertParentsConsistent();
    }

    /// <summary>
    /// У каждой записи <c>parent_id</c> указывает на запись родительской папки, у верхнего уровня — <c>NULL</c>.
    /// </summary>
    public void AssertParentsConsistent()
    {
        using var connection = Database.Connections.Open();
        var rows = connection.Query<(long Id, string RelPath, long? ParentId)>(
            "SELECT id, rel_path, parent_id FROM entries WHERE root_id = @RootId", new { RootId = Root.Id }).ToList();
        var ids = rows.ToDictionary(static row => row.RelPath, static row => row.Id, StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var parent = Factory.Parent(row.RelPath);
            if (parent.Length == 0)
            {
                row.ParentId.ShouldBeNull($"у «{row.RelPath}» не должно быть родителя");
            }
            else
            {
                row.ParentId.ShouldBe(ids[parent], $"у «{row.RelPath}» неверный родитель");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Database.DisposeAsync();
        Data.Dispose();
        Files.Dispose();
    }
}

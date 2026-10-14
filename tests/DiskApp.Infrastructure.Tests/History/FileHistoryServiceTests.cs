using DiskApp.Core.Abstractions;
using DiskApp.Core.Events;
using DiskApp.Core.History;
using DiskApp.Core.Operations;
using DiskApp.Core.Paths;
using DiskApp.Core.Results;
using DiskApp.Core.Settings;
using DiskApp.Infrastructure.Sqlite;
using DiskApp.Infrastructure.Sqlite.Repositories;
using DiskApp.Infrastructure.Tests.Sqlite.Repositories;

namespace DiskApp.Infrastructure.Tests.History;

/// <summary>
/// История файлов (PLAN.md, этап 20) на настоящей базе и папках: перенос в Проводнике собирается из «исчез + появился»,
/// переименование, временный файл и загрузка браузера, удаление в корзину и навсегда; операция DiskApp пишется один раз
/// со своим источником («разбор»), а её эхо от наблюдателя отбрасывается; сверка и сводка индекса; история файла через
/// переносы; «Вернуть на место» и «Восстановить»; теги; срок хранения и предел размера.
/// </summary>
public sealed class FileHistoryServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Created = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Modified = new(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);

    private readonly TemporaryDirectory _files = new("history-files");
    private readonly TemporaryDirectory _data = new("history-data");
    private readonly IndexEventBus _events = new();
    private readonly TestClock _clock = new(IndexTestContext.Now);
    private readonly MemorySettingsStore _settings = new();
    private readonly FakeRecycleBin _recycleBin = new();
    private SqliteDatabase _database = null!;
    private FileOperationService _operations = null!;
    private FileHistoryService _history = null!;

    public async ValueTask InitializeAsync()
    {
        _database = new SqliteDatabase(_data.Resolve("index.db"));
        await _database.InitializeAsync(TestContext.Current.CancellationToken);
        var shell = Substitute.For<IPlatformShell>();
        shell.MoveToRecycleBinAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            foreach (var path in call.ArgAt<IReadOnlyList<string>>(0))
            {
                _recycleBin.Recycle(path);
            }

            return Result.Ok();
        });
        _operations = new FileOperationService(new System.IO.Abstractions.FileSystem(), _events, _clock, PathNormalizer.Current, shell: shell);
        _history = CreateHistory();
    }

    public async ValueTask DisposeAsync()
    {
        await _history.DisposeAsync();
        await _operations.DisposeAsync();
        await _database.DisposeAsync();
        _files.Dispose();
        _data.Dispose();
    }

    [Fact]
    public async Task Explorer_RemovedHereAddedThere_IsOneMove()
    {
        // Act: наблюдатель видит перенос между папками разных корней как «исчез» в одном и «появился» в другом
        _events.Publish(new IndexJournalEvent(1, JournalSource.Watcher, [Removed("a/акт.pdf")]));
        _events.Publish(new IndexJournalEvent(2, JournalSource.Watcher, [Added("b/акт.pdf")]));
        var events = await FlushAsync();

        // Assert
        var moved = events.ShouldHaveSingleItem();
        (moved.Kind, moved.Source, moved.Path, moved.OldPath).ShouldBe((FileEventKind.Moved, FileEventSource.Explorer, Path("b/акт.pdf"), Path("a/акт.pdf")));
        moved.Size.ShouldBe(10);
    }

    [Fact]
    public async Task Explorer_RenameTemporaryRecycledAndGone()
    {
        // Arrange: в корзине лежит только «старый.docx»
        _recycleBin.Items[Path("docs/старый.docx")] = Path("bin/$Rold.docx");

        // Act
        Publish(
            JournalSource.Watcher,
            new JournalChange(JournalChangeKind.Renamed, Path("docs/новое имя.txt"), Path("docs/имя.txt"), false, 5, Created, Modified),
            new JournalChange(JournalChangeKind.Renamed, Path("Загрузки/отчёт.pdf"), Path("Загрузки/отчёт.pdf.crdownload"), false, 7, Created, Modified),
            Added("Загрузки/~tmp.bin"),
            Removed("Загрузки/~tmp.bin"),
            Removed("docs/старый.docx"),
            Removed("docs/потерянный.txt", size: 3));
        var events = await FlushAsync();

        // Assert: временный файл не пишется; переименование загрузки браузера — появление файла
        events.Select(static item => (item.Kind, item.Name)).ShouldBe(
        [
            (FileEventKind.Renamed, "новое имя.txt"),
            (FileEventKind.Created, "отчёт.pdf"),
            (FileEventKind.Recycled, "старый.docx"),
            (FileEventKind.Deleted, "потерянный.txt"),
        ], ignoreOrder: true);
        events.Single(static item => item.Kind == FileEventKind.Recycled).Details.ShouldBe(Path("bin/$Rold.docx"));
        events.Single(static item => item.Kind == FileEventKind.Renamed).OldName.ShouldBe("имя.txt");
    }

    [Fact]
    public async Task DiskAppMove_RecordedOnceWithSource_WatcherEchoDropped()
    {
        // Arrange
        var source = _files.CreateFile(System.IO.Path.Combine("Загрузки", "акт.pdf"), "0123456789");
        var target = _files.CreateDirectory(System.IO.Path.Combine("417", "Акты"));

        // Act: перенос «Разбора», и наблюдатель видит его же
        var operation = Enqueue(new FileOperationRequest(FileOperationKind.Move, [source], target, Origin: OperationOrigin.Sorting));
        await _operations.WhenIdleAsync(TestContext.Current.CancellationToken);
        Publish(JournalSource.Watcher, Removed("Загрузки/акт.pdf"), Added("417/Акты/акт.pdf"));
        var events = await FlushAsync();

        // Assert
        var moved = events.ShouldHaveSingleItem();
        (moved.Kind, moved.Source, moved.OperationId, moved.Path, moved.OldPath, moved.Size).ShouldBe(
            (FileEventKind.Moved, FileEventSource.Sorting, operation.Id, System.IO.Path.Combine(target, "акт.pdf"), source, 10L));
    }

    [Fact]
    public async Task DiskAppDelete_ToRecycleBinWithSize_PermanentlyAsDeleted()
    {
        // Arrange
        var recycled = _files.CreateFile("черновик.txt", "12345");
        var permanent = _files.CreateFile("мусор.txt", "123");

        // Act
        Enqueue(new FileOperationRequest(FileOperationKind.Delete, [recycled]));
        Enqueue(new FileOperationRequest(FileOperationKind.Delete, [permanent], Permanent: true));
        await _operations.WhenIdleAsync(TestContext.Current.CancellationToken);
        var events = await FlushAsync();

        // Assert
        events.Select(static item => (item.Kind, item.Source, item.Name, item.Size)).ShouldBe(
        [
            (FileEventKind.Recycled, FileEventSource.DiskApp, "черновик.txt", 5L),
            (FileEventKind.Deleted, FileEventSource.DiskApp, "мусор.txt", 3L),
        ], ignoreOrder: true);
        events.Single(static item => item.Kind == FileEventKind.Recycled).Details.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Reconcile_ChangesAndScanSummary()
    {
        // Act: сверка нашла перенос, сделанный без DiskApp; первое сканирование корня — сводка
        Publish(JournalSource.Reconcile, Removed("a/акт.pdf"), Added("b/акт.pdf"));
        _events.Publish(new IndexJournalEvent(1, JournalSource.Scan, []) { RootPath = _files.FullPath, Summary = "Первое сканирование", Count = 1234 });
        var events = await FlushAsync();

        // Assert
        events.Select(static item => (item.Kind, item.Source)).ShouldBe([(FileEventKind.Moved, FileEventSource.Reconcile), (FileEventKind.IndexSummary, FileEventSource.Reconcile)], ignoreOrder: true);
        var summary = events.Single(static item => item.Kind == FileEventKind.IndexSummary);
        (summary.Path, summary.Size, summary.Details).ShouldBe((_files.FullPath, 1234L, "Первое сканирование"));
    }

    [Fact]
    public async Task FileHistory_FollowsMovesAndRenamesBack()
    {
        // Arrange: файл появился в «Загрузках», уехал в 417 и там переименован
        Publish(JournalSource.Watcher, Added("Загрузки/акт.pdf"));
        await FlushAsync();
        Publish(JournalSource.Watcher, Removed("Загрузки/акт.pdf"), Added("417/акт.pdf"));
        await FlushAsync();
        Publish(JournalSource.Watcher, new JournalChange(JournalChangeKind.Renamed, Path("417/акт сверки.pdf"), Path("417/акт.pdf"), false, 10, Created, Modified));
        await FlushAsync();
        Publish(JournalSource.Watcher, Added("другой.pdf"));
        await FlushAsync();

        // Act
        var chain = await _history.GetFileHistoryAsync(Path("417/акт сверки.pdf"), TestContext.Current.CancellationToken);

        // Assert: новые сверху
        chain.Select(static item => item.Kind).ShouldBe([FileEventKind.Renamed, FileEventKind.Moved, FileEventKind.Created]);
    }

    [Fact]
    public async Task Revert_MoveGoesBackAfterRestart_PlaceTakenIsRefused()
    {
        // Arrange: файл перенесли в Проводнике
        var from = _files.CreateFile(System.IO.Path.Combine("a", "акт.pdf"), "0123456789");
        _files.CreateDirectory("b");
        File.Move(from, Path("b/акт.pdf"));
        Publish(JournalSource.Watcher, Removed("a/акт.pdf"), Added("b/акт.pdf"));
        var moved = (await FlushAsync()).ShouldHaveSingleItem();

        // Act: после перезапуска всё нужное — в событии
        await _history.DisposeAsync();
        _history = CreateHistory();
        var reverted = await _history.RevertAsync(moved.Id, TestContext.Current.CancellationToken);
        await _operations.WhenIdleAsync(TestContext.Current.CancellationToken);
        var again = await _history.RevertAsync(moved.Id, TestContext.Current.CancellationToken);

        // Assert
        reverted.IsSuccess.ShouldBeTrue();
        File.Exists(from).ShouldBeTrue();
        File.Exists(Path("b/акт.pdf")).ShouldBeFalse();
        again.Error!.Code.ShouldBe(FileHistoryService.CannotRevertCode);
    }

    [Fact]
    public async Task Revert_Recycled_RestoresFromRecycleBinAndRecordsIt()
    {
        // Arrange
        var file = _files.CreateFile("договор.docx", "123");
        Enqueue(new FileOperationRequest(FileOperationKind.Delete, [file]));
        await _operations.WhenIdleAsync(TestContext.Current.CancellationToken);
        var recycled = (await FlushAsync()).ShouldHaveSingleItem();

        // Act: наблюдатель видит восстановленный файл как появившийся
        var restored = await _history.RevertAsync(recycled.Id, TestContext.Current.CancellationToken);
        Publish(JournalSource.Watcher, Added("договор.docx", size: 3));
        var echo = await FlushAsync();

        // Assert
        File.Exists(file).ShouldBeTrue();
        restored.Value.Restored!.Kind.ShouldBe(FileEventKind.Restored);
        echo.ShouldBeEmpty();
    }

    [Fact]
    public async Task Revert_Recycled_StaleBinPathFoundAgain_EmptiedBinRefused()
    {
        // Arrange: файл удалили в Проводнике; потом корзина переложила его под другим именем
        var file = _files.CreateFile("смета.xlsx", "1234");
        _recycleBin.Recycle(file);
        Publish(JournalSource.Watcher, Removed("смета.xlsx", size: 4));
        var recycled = (await FlushAsync()).ShouldHaveSingleItem();
        var moved = _recycleBin.Items[file] + "2";
        File.Move(_recycleBin.Items[file], moved);
        _recycleBin.Items[file] = moved;
        var other = _files.CreateFile("старое.txt", "1");
        _recycleBin.Recycle(other);
        Publish(JournalSource.Watcher, Removed("старое.txt", size: 1));
        var emptied = (await FlushAsync()).ShouldHaveSingleItem();
        File.Delete(_recycleBin.Items[other]);
        _recycleBin.Items.Remove(other);

        // Act
        var restored = await _history.RevertAsync(recycled.Id, TestContext.Current.CancellationToken);
        var refused = await _history.RevertAsync(emptied.Id, TestContext.Current.CancellationToken);

        // Assert
        restored.IsSuccess.ShouldBeTrue();
        File.ReadAllText(file).ShouldBe("1234");
        refused.Error!.Code.ShouldBe(FileHistoryService.CannotRevertCode);
    }

    [Fact]
    public async Task Tags_RecordedWithAddedAndRemoved()
    {
        // Act
        await _history.RecordTagsChangedAsync(Path("акт.pdf"), false, ["417"], ["черновик"], TestContext.Current.CancellationToken);
        var events = await _history.QueryAsync(new FileHistoryQuery { Kinds = [FileEventKind.TagsChanged] }, TestContext.Current.CancellationToken);

        // Assert
        events.ShouldHaveSingleItem().Details.ShouldBe("+417, −черновик");
    }

    [Fact]
    public async Task Cleanup_RetentionThenSizeLimit()
    {
        // Arrange: сто старых событий и тысяча свежих с длинными путями
        var repository = new SqliteFileHistoryRepository(_database, PathNormalizer.Current);
        var longName = new string('я', 200);
        await repository.AddAsync([.. Enumerable.Range(0, 100).Select(i => Event(_clock.UtcNow.AddDays(-100), $"old/{i}.txt"))], TestContext.Current.CancellationToken);
        await repository.AddAsync([.. Enumerable.Range(0, 1_000).Select(i => Event(_clock.UtcNow, $"{longName}/{i}.txt"))], TestContext.Current.CancellationToken);
        _settings.Current = new AppSettings { History = new HistorySettings { RetentionDays = 90, MaxSizeMegabytes = 1 } };
        var before = await repository.EstimateSizeAsync(TestContext.Current.CancellationToken);

        // Act
        var removed = await _history.CleanupAsync(TestContext.Current.CancellationToken);

        // Assert
        before.ShouldBeGreaterThan(1024 * 1024);
        removed.ShouldBeGreaterThan(100);
        (await repository.EstimateSizeAsync(TestContext.Current.CancellationToken)).ShouldBeLessThanOrEqualTo(1024 * 1024);
        (await repository.QueryAsync(new FileHistoryQuery { Text = "0.txt", Limit = 500 }, TestContext.Current.CancellationToken)).ShouldAllBe(item => !item.Path.StartsWith(Path("old"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Query_FolderTextKindsAndPaging()
    {
        // Arrange
        Publish(JournalSource.Watcher, Removed("a/старое имя.pdf"), Added("b/старое имя.pdf"));
        await FlushAsync();
        Publish(JournalSource.Watcher, new JournalChange(JournalChangeKind.Renamed, Path("b/новое.pdf"), Path("b/старое имя.pdf"), false, 10, Created, Modified));
        await FlushAsync();
        Publish(JournalSource.Watcher, Added("c/x1.txt"), Added("c/x2.txt"), Added("c/x3.txt"));
        await FlushAsync();
        var ct = TestContext.Current.CancellationToken;

        // Act
        var inA = await _history.QueryAsync(new FileHistoryQuery { Folder = Path("a") }, ct);
        var byOldName = await _history.QueryAsync(new FileHistoryQuery { Text = "СТАРОЕ" }, ct);
        var renames = await _history.QueryAsync(new FileHistoryQuery { Kinds = [FileEventKind.Renamed] }, ct);
        var firstPage = await _history.QueryAsync(new FileHistoryQuery { Folder = Path("c"), Limit = 2 }, ct);
        var secondPage = await _history.QueryAsync(new FileHistoryQuery { Folder = Path("c"), Limit = 2, BeforeId = firstPage[^1].Id }, ct);

        // Assert: в папке «a» — перенос оттуда; по прежнему имени — оба события
        inA.ShouldHaveSingleItem().Kind.ShouldBe(FileEventKind.Moved);
        byOldName.Count.ShouldBe(2);
        renames.ShouldHaveSingleItem().Name.ShouldBe("новое.pdf");
        firstPage.Count.ShouldBe(2);
        secondPage.ShouldHaveSingleItem();
        firstPage.Concat(secondPage).Select(static item => item.Id).ShouldBeUnique();
    }

    private FileHistoryService CreateHistory()
    {
        var history = new FileHistoryService(new SqliteFileHistoryRepository(_database, PathNormalizer.Current), _events, _operations, _settings, _clock, PathNormalizer.Current, new System.IO.Abstractions.FileSystem(), _recycleBin)
        {
            // Тест сам решает, когда записать накопленное.
            TickInterval = TimeSpan.FromHours(1),
        };
        history.Start();
        return history;
    }

    private string Path(string relative) => System.IO.Path.Combine(_files.FullPath, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private JournalChange Removed(string relative, long size = 10) => new(JournalChangeKind.Removed, Path(relative), null, false, size, Created, Modified);

    private JournalChange Added(string relative, long size = 10) => new(JournalChangeKind.Added, Path(relative), null, false, size, Created, Modified);

    private FileEvent Event(DateTimeOffset time, string relative) => new(0, time, FileEventKind.Created, FileEventSource.Explorer, Path(relative), null, false, 1);

    private void Publish(JournalSource source, params JournalChange[] changes) => _events.Publish(new IndexJournalEvent(1, source, changes));

    private FileOperationInfo Enqueue(FileOperationRequest request)
    {
        var result = _operations.Enqueue(request);
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        return result.Value;
    }

    // Записывает накопленное и возвращает записанное этим вызовом.
    private async Task<IReadOnlyList<FileEvent>> FlushAsync()
    {
        var last = await _history.QueryAsync(new FileHistoryQuery { Limit = 1 }, TestContext.Current.CancellationToken);
        var before = last.Count > 0 ? last[0].Id : 0;
        await _history.FlushAsync(TestContext.Current.CancellationToken);
        var all = await _history.QueryAsync(new FileHistoryQuery { Limit = 500 }, TestContext.Current.CancellationToken);
        return [.. all.Where(item => item.Id > before)];
    }

    private sealed class FakeRecycleBin : IRecycleBin
    {
        public Dictionary<string, string> Items { get; } = new(StringComparer.Ordinal);

        public void Recycle(string path)
        {
            var stored = path + ".recycled";
            File.Move(path, stored);
            Items[path] = stored;
        }

        public RecycledItem? Find(string originalPath)
            => Items.TryGetValue(originalPath, out var stored) ? new RecycledItem(originalPath, stored, DateTimeOffset.UtcNow, 0) : null;

        public Result Restore(string storedPath, string originalPath)
        {
            File.Move(storedPath, originalPath);
            Items.Remove(originalPath);
            return Result.Ok();
        }
    }

    private sealed class MemorySettingsStore : ISettingsStore<AppSettings>
    {
        public AppSettings Current { get; set; } = new();

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Current = settings;
            return Task.CompletedTask;
        }

        public IDisposable Subscribe(Action<AppSettings> onChanged) => new Unsubscriber();

        private sealed class Unsubscriber : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}

using System.Collections.Concurrent;

using DiskApp.Core.Abstractions;
using DiskApp.Core.Events;
using DiskApp.Core.Indexing;
using DiskApp.Core.Operations;
using DiskApp.Core.Paths;
using DiskApp.Core.Results;

namespace DiskApp.Infrastructure.Tests.Operations;

/// <summary>
/// Файловые операции на временных папках (PLAN.md, этап 4.5): копирование с сохранением времён и атрибутов, конфликты
/// по политике и по решению пользователя, отмена посередине без недописанных файлов, перемещение в пределах тома
/// и между томами, слияние папок, переименование, удаление в корзину и безвозвратно, создание, отмена результата.
/// </summary>
public sealed class FileOperationServiceTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private readonly TemporaryDirectory _root = new("ops");
    private readonly IndexEventBus _events = new();
    private readonly RecordingSink _sink = new();
    private readonly RecycleShell _shell;
    private FileOperationService? _service;

    public FileOperationServiceTests()
    {
        _shell = new RecycleShell(_root.CreateDirectory("_trash"));
    }

    [Fact]
    public async Task Copy_FileAndFolder_CopiesContentTimesAndAttributes()
    {
        // Arrange
        var file = _root.CreateFile(Path.Combine("src", "report.txt"), "квартальный отчёт");
        var stamp = new DateTime(2024, 3, 15, 10, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, stamp);
        File.SetAttributes(file, FileAttributes.Archive | FileAttributes.Hidden);
        _root.CreateFile(Path.Combine("src", "docs", "inner", "deep.md"), "deep");
        var destination = _root.CreateDirectory("dest");
        var progress = new ConcurrentQueue<OperationProgressEvent>();
        using var subscription = _events.Subscribe<OperationProgressEvent>(progress.Enqueue);
        var service = Service();

        // Act
        var info = await RunAsync(service, new FileOperationRequest(FileOperationKind.Copy, [file, _root.Resolve(Path.Combine("src", "docs"))], destination));

        // Assert
        info.State.ShouldBe(OperationState.Completed, info.Message);
        var copy = Path.Combine(destination, "report.txt");
        (await File.ReadAllTextAsync(copy, TestContext.Current.CancellationToken)).ShouldBe("квартальный отчёт");
        File.GetLastWriteTimeUtc(copy).ShouldBe(stamp);
        (File.GetAttributes(copy) & FileAttributes.Hidden).ShouldBe(FileAttributes.Hidden);
        File.ReadAllText(Path.Combine(destination, "docs", "inner", "deep.md")).ShouldBe("deep");
        File.Exists(file).ShouldBeTrue("копирование не трогает источник");
        info.Progress.ProcessedItems.ShouldBe(info.Progress.TotalItems);
        info.Progress.TotalBytes.ShouldBe(new FileInfo(file).Length + 4);
        progress.ShouldNotBeEmpty();
        _sink.Paths.ShouldContain(destination);
        info.CanUndo.ShouldBeTrue("копирование отменяется: копии уходят в корзину");
    }

    [Fact]
    public async Task Copy_Undo_RecyclesOnlyCreatedCopies()
    {
        // Arrange: файл, новая папка и файл, одноимённый уже лежащему в назначении (оставить оба)
        var file = _root.CreateFile(Path.Combine("src", "report.txt"), "новый");
        var folder = _root.CreateDirectory(Path.Combine("src", "photos"));
        _root.CreateFile(Path.Combine("src", "photos", "a.jpg"), "a");
        var destination = _root.CreateDirectory("dest");
        var existing = _root.CreateFile(Path.Combine("dest", "report.txt"), "старый");
        var service = Service();
        var copied = await RunAsync(service, new FileOperationRequest(FileOperationKind.Copy, [file, folder], destination, Conflicts: ConflictPolicy.KeepBoth));

        // Act
        var undo = service.Undo(copied.Id);
        await service.WhenIdleAsync(TestContext.Current.CancellationToken).WaitAsync(Wait, TestContext.Current.CancellationToken);

        // Assert
        copied.State.ShouldBe(OperationState.Completed, copied.Message);
        undo.IsSuccess.ShouldBeTrue(undo.Error?.Message);
        _shell.Recycled.ShouldBe([Path.Combine(destination, "report (2).txt"), Path.Combine(destination, "photos")], ignoreOrder: true);
        File.ReadAllText(existing).ShouldBe("старый", "лежавший раньше файл не тронут");
        File.Exists(file).ShouldBeTrue("источник копирования не тронут");
        service.Get(copied.Id)!.CanUndo.ShouldBeFalse("повторная отмена невозможна");
    }

    [Fact]
    public async Task Copy_IntoSameFolder_KeepsBothWithSuffix()
    {
        // Arrange
        var file = _root.CreateFile(Path.Combine("src", "photo.jpg"), "jpg");
        var service = Service();

        // Act
        var first = await RunAsync(service, new FileOperationRequest(FileOperationKind.Copy, [file], _root.Resolve("src")));
        var second = await RunAsync(service, new FileOperationRequest(FileOperationKind.Copy, [file], _root.Resolve("src")));

        // Assert
        first.State.ShouldBe(OperationState.Completed);
        second.State.ShouldBe(OperationState.Completed);
        File.Exists(_root.Resolve(Path.Combine("src", "photo (2).jpg"))).ShouldBeTrue();
        File.Exists(_root.Resolve(Path.Combine("src", "photo (3).jpg"))).ShouldBeTrue();
    }

    [Theory]
    [InlineData(ConflictPolicy.Skip, "old", false)]
    [InlineData(ConflictPolicy.KeepBoth, "old", false)]
    [InlineData(ConflictPolicy.Overwrite, "new", true)]
    public async Task Copy_ConflictPolicy_IsApplied(ConflictPolicy policy, string expectedTarget, bool recycled)
    {
        // Arrange
        var source = _root.CreateFile(Path.Combine("src", "plan.docx"), "new");
        var target = _root.CreateFile(Path.Combine("dest", "plan.docx"), "old");
        var service = Service();

        // Act
        var info = await RunAsync(service, new FileOperationRequest(FileOperationKind.Copy, [source], _root.Resolve("dest"), Conflicts: policy));

        // Assert
        info.State.ShouldBe(OperationState.Completed, info.Message);
        File.ReadAllText(target).ShouldBe(expectedTarget);
        File.Exists(_root.Resolve(Path.Combine("dest", "plan (2).docx"))).ShouldBe(policy == ConflictPolicy.KeepBoth);
        _shell.Recycled.Contains(target).ShouldBe(recycled, "заменяемый файл уходит в корзину");
    }

    [Fact]
    public async Task Copy_AskConflict_WaitsForDecisionAndAppliesToAll()
    {
        // Arrange
        var a = _root.CreateFile(Path.Combine("src", "a.txt"), "a-new");
        var b = _root.CreateFile(Path.Combine("src", "b.txt"), "b-new");
        _root.CreateFile(Path.Combine("dest", "a.txt"), "a-old");
        _root.CreateFile(Path.Combine("dest", "b.txt"), "b-old");
        var asked = new ConcurrentQueue<OperationNeedsDecisionEvent>();
        using var subscription = _events.Subscribe<OperationNeedsDecisionEvent>(asked.Enqueue);
        var service = Service();

        // Act
        var queued = service.Enqueue(new FileOperationRequest(FileOperationKind.Copy, [a, b], _root.Resolve("dest"))).Value;
        await WaitUntilAsync(() => service.Get(queued.Id)!.State == OperationState.WaitingDecision);
        var waiting = service.Get(queued.Id)!;
        service.Decide(queued.Id, ConflictResolution.KeepBoth, applyToAll: true).IsSuccess.ShouldBeTrue();
        await service.WhenIdleAsync(TestContext.Current.CancellationToken).WaitAsync(Wait, TestContext.Current.CancellationToken);
        var done = service.Get(queued.Id)!;

        // Assert
        waiting.PendingConflict.ShouldNotBeNull();
        waiting.PendingConflict.TargetPath.ShouldBe(_root.Resolve(Path.Combine("dest", "a.txt")));
        waiting.PendingConflict.SourceSize.ShouldBe(5);
        asked.Count.ShouldBe(1, "второй конфликт решён по «применить ко всем»");
        done.State.ShouldBe(OperationState.Completed);
        File.ReadAllText(_root.Resolve(Path.Combine("dest", "a (2).txt"))).ShouldBe("a-new");
        File.ReadAllText(_root.Resolve(Path.Combine("dest", "b (2).txt"))).ShouldBe("b-new");
        service.Decide(queued.Id, ConflictResolution.Skip, false).Error!.Code.ShouldBe(FileOperationService.NoConflictCode);
    }

    [Fact]
    public async Task Copy_AskThenCancelDecision_CancelsOperation()
    {
        // Arrange
        var a = _root.CreateFile(Path.Combine("src", "a.txt"), "a");
        _root.CreateFile(Path.Combine("dest", "a.txt"), "old");
        var service = Service();

        // Act
        var queued = service.Enqueue(new FileOperationRequest(FileOperationKind.Copy, [a], _root.Resolve("dest"))).Value;
        await WaitUntilAsync(() => service.Get(queued.Id)!.State == OperationState.WaitingDecision);
        service.Decide(queued.Id, ConflictResolution.Cancel, applyToAll: false);
        await service.WhenIdleAsync(TestContext.Current.CancellationToken).WaitAsync(Wait, TestContext.Current.CancellationToken);

        // Assert
        service.Get(queued.Id)!.State.ShouldBe(OperationState.Cancelled);
        File.ReadAllText(_root.Resolve(Path.Combine("dest", "a.txt"))).ShouldBe("old");
    }

    [Fact]
    public async Task Cancel_DuringLargeCopy_LeavesNoPartialFile()
    {
        // Arrange
        var big = _root.Resolve(Path.Combine("src", "big.bin"));
        Directory.CreateDirectory(Path.GetDirectoryName(big)!);
        await File.WriteAllBytesAsync(big, new byte[16 * 1024 * 1024], TestContext.Current.CancellationToken);
        var destination = _root.CreateDirectory("dest");
        var service = Service(new FileOperationOptions { BufferSize = 4 * 1024, ProgressInterval = TimeSpan.Zero });
        Guid id = default;
        var cancelled = 0;
        using var subscription = _events.Subscribe<OperationProgressEvent>(e =>
        {
            if (e.ProcessedBytes > 0 && Interlocked.Exchange(ref cancelled, 1) == 0)
            {
                service.Cancel(e.OperationId);
            }
        });

        // Act
        id = service.Enqueue(new FileOperationRequest(FileOperationKind.Copy, [big], destination)).Value.Id;
        await service.WhenIdleAsync(TestContext.Current.CancellationToken).WaitAsync(Wait, TestContext.Current.CancellationToken);

        // Assert
        service.Get(id)!.State.ShouldBe(OperationState.Cancelled);
        File.Exists(Path.Combine(destination, "big.bin")).ShouldBeFalse("недописанный файл удаляется");
        File.Exists(big).ShouldBeTrue();
        service.Cancel(id).Error!.Code.ShouldBe(FileOperationService.FinishedCode);
    }

    [Fact]
    public async Task Move_SameVolume_MovesAndUndoRestores()
    {
        // Arrange
        var file = _root.CreateFile(Path.Combine("inbox", "invoice.pdf"), "pdf");
        _root.CreateFile(Path.Combine("inbox", "scans", "s1.png"), "png");
        var destination = _root.CreateDirectory("archive");
        var service = Service();

        // Act
        var moved = await RunAsync(service, new FileOperationRequest(FileOperationKind.Move, [file, _root.Resolve(Path.Combine("inbox", "scans"))], destination));
        var afterMove = (File.Exists(Path.Combine(destination, "invoice.pdf")), Directory.Exists(Path.Combine(destination, "scans")), File.Exists(file));
        var undo = service.Undo(moved.Id);
        await service.WhenIdleAsync(TestContext.Current.CancellationToken).WaitAsync(Wait, TestContext.Current.CancellationToken);

        // Assert
        moved.State.ShouldBe(OperationState.Completed, moved.Message);
        afterMove.ShouldBe((true, true, false));
        moved.CanUndo.ShouldBeTrue();
        undo.IsSuccess.ShouldBeTrue();
        service.Get(undo.Value.Id)!.State.ShouldBe(OperationState.Completed, service.Get(undo.Value.Id)!.Message);
        File.Exists(file).ShouldBeTrue();
        File.Exists(_root.Resolve(Path.Combine("inbox", "scans", "s1.png"))).ShouldBeTrue();
        File.Exists(Path.Combine(destination, "invoice.pdf")).ShouldBeFalse();
        service.Get(moved.Id)!.CanUndo.ShouldBeFalse("повторная отмена невозможна");
        service.Undo(moved.Id).Error!.Code.ShouldBe(FileOperationService.NotUndoableCode);
        _sink.Paths.ShouldContain(_root.Resolve("inbox"));
    }

    [Fact]
    public async Task Move_CrossVolume_CopiesDeletesSourceAndUndoRestores()
    {
        // Arrange
        var file = _root.CreateFile(Path.Combine("c", "notes.txt"), "заметки");
        _root.CreateFile(Path.Combine("c", "project", "src", "main.cs"), "code");
        var destination = _root.CreateDirectory("d");
        var service = Service(new FileOperationOptions { SameVolume = static (_, _) => false });

        // Act
        var moved = await RunAsync(service, new FileOperationRequest(FileOperationKind.Move, [file, _root.Resolve(Path.Combine("c", "project"))], destination));
        var afterMove = (File.ReadAllText(Path.Combine(destination, "notes.txt")), File.Exists(Path.Combine(destination, "project", "src", "main.cs")), File.Exists(file), Directory.Exists(_root.Resolve(Path.Combine("c", "project"))));
        var undo = service.Undo(moved.Id).Value;
        await service.WhenIdleAsync(TestContext.Current.CancellationToken).WaitAsync(Wait, TestContext.Current.CancellationToken);

        // Assert
        moved.State.ShouldBe(OperationState.Completed, moved.Message);
        afterMove.ShouldBe(("заметки", true, false, false));
        service.Get(undo.Id)!.State.ShouldBe(OperationState.Completed, service.Get(undo.Id)!.Message);
        File.ReadAllText(file).ShouldBe("заметки");
        File.Exists(_root.Resolve(Path.Combine("c", "project", "src", "main.cs"))).ShouldBeTrue();
        Directory.Exists(Path.Combine(destination, "project")).ShouldBeFalse();
    }

    [Fact]
    public async Task Move_IntoExistingFolder_MergesContents()
    {
        // Arrange
        _root.CreateFile(Path.Combine("from", "photos", "new.jpg"), "n");
        _root.CreateFile(Path.Combine("to", "photos", "old.jpg"), "o");
        var service = Service();

        // Act
        var info = await RunAsync(service, new FileOperationRequest(FileOperationKind.Move, [_root.Resolve(Path.Combine("from", "photos"))], _root.Resolve("to"), Conflicts: ConflictPolicy.Overwrite));

        // Assert
        info.State.ShouldBe(OperationState.Completed, info.Message);
        File.Exists(_root.Resolve(Path.Combine("to", "photos", "new.jpg"))).ShouldBeTrue();
        File.Exists(_root.Resolve(Path.Combine("to", "photos", "old.jpg"))).ShouldBeTrue();
        Directory.Exists(_root.Resolve(Path.Combine("from", "photos"))).ShouldBeFalse();
        _shell.Recycled.ShouldBeEmpty("при слиянии папок ничего не удаляется");
    }

    [Fact]
    public async Task Rename_FileFolderAndCaseOnly_AndUndo()
    {
        // Arrange
        var file = _root.CreateFile(Path.Combine("docs", "draft.txt"), "d");
        var folder = _root.CreateDirectory(Path.Combine("docs", "old"));
        var service = Service();

        // Act
        var renamed = await RunAsync(service, new FileOperationRequest(FileOperationKind.Rename, [file], NewName: "final.txt"));
        var folderRenamed = await RunAsync(service, new FileOperationRequest(FileOperationKind.Rename, [folder], NewName: "Old"));
        var names = Directory.GetFileSystemEntries(_root.Resolve("docs")).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        service.Undo(renamed.Id).IsSuccess.ShouldBeTrue();
        await service.WhenIdleAsync(TestContext.Current.CancellationToken).WaitAsync(Wait, TestContext.Current.CancellationToken);

        // Assert
        renamed.State.ShouldBe(OperationState.Completed, renamed.Message);
        folderRenamed.State.ShouldBe(OperationState.Completed, folderRenamed.Message);
        names.ShouldBe(["Old", "final.txt"]);
        File.Exists(file).ShouldBeTrue("отмена вернула прежнее имя");
    }

    [Fact]
    public async Task Delete_ToRecycleBinOrPermanently()
    {
        // Arrange
        var recycled = _root.CreateFile(Path.Combine("x", "to-trash.txt"), "t");
        var permanent = _root.CreateFile(Path.Combine("x", "gone", "readonly.txt"), "r");
        File.SetAttributes(permanent, FileAttributes.ReadOnly);
        var service = Service();

        // Act
        var toTrash = await RunAsync(service, new FileOperationRequest(FileOperationKind.Delete, [recycled]));
        var gone = await RunAsync(service, new FileOperationRequest(FileOperationKind.Delete, [_root.Resolve(Path.Combine("x", "gone"))], Permanent: true));

        // Assert
        toTrash.State.ShouldBe(OperationState.Completed, toTrash.Message);
        _shell.Recycled.ShouldBe([recycled]);
        File.Exists(recycled).ShouldBeFalse();
        gone.State.ShouldBe(OperationState.Completed, gone.Message);
        Directory.Exists(_root.Resolve(Path.Combine("x", "gone"))).ShouldBeFalse();
        _shell.Recycled.Count.ShouldBe(1, "безвозвратное удаление не идёт через корзину");
    }

    [Fact]
    public async Task Delete_WithoutRecycleBin_FailsAndKeepsFile()
    {
        // Arrange
        var file = _root.CreateFile("keep.txt", "k");
        await using var service = new FileOperationService(new System.IO.Abstractions.FileSystem(), _events, SystemClock.Instance, PathNormalizer.Current);

        // Act
        var info = await RunAsync(service, new FileOperationRequest(FileOperationKind.Delete, [file]));

        // Assert
        info.State.ShouldBe(OperationState.Failed);
        info.Message.ShouldNotBeNull().ShouldContain("Корзина");
        File.Exists(file).ShouldBeTrue();
    }

    [Fact]
    public async Task Create_FolderAndFile_KeepsBothAndUndoRecycles()
    {
        // Arrange
        var parent = _root.CreateDirectory("work");
        _root.CreateDirectory(Path.Combine("work", "Новая папка"));
        var service = Service();

        // Act
        var folder = await RunAsync(service, new FileOperationRequest(FileOperationKind.CreateFolder, [], parent, "Новая папка"));
        var file = await RunAsync(service, new FileOperationRequest(FileOperationKind.CreateFile, [], parent, "todo.txt"));
        service.Undo(folder.Id).IsSuccess.ShouldBeTrue();
        await service.WhenIdleAsync(TestContext.Current.CancellationToken).WaitAsync(Wait, TestContext.Current.CancellationToken);

        // Assert
        folder.State.ShouldBe(OperationState.Completed, folder.Message);
        file.State.ShouldBe(OperationState.Completed, file.Message);
        new FileInfo(Path.Combine(parent, "todo.txt")).Length.ShouldBe(0);
        Directory.Exists(Path.Combine(parent, "Новая папка (2)")).ShouldBeFalse("отменённое создание ушло в корзину");
        _shell.Recycled.ShouldBe([Path.Combine(parent, "Новая папка (2)")]);
    }

    [Fact]
    public void Enqueue_InvalidRequests_AreRejected()
    {
        // Arrange
        var folder = _root.CreateDirectory("folder");
        var file = _root.CreateFile(Path.Combine("folder", "f.txt"), "f");
        var service = Service();

        // Act
        var results = new[]
        {
            service.Enqueue(new FileOperationRequest(FileOperationKind.Copy, [folder], _root.Resolve(Path.Combine("folder")))),
            service.Enqueue(new FileOperationRequest(FileOperationKind.Copy, ["relative.txt"], folder)),
            service.Enqueue(new FileOperationRequest(FileOperationKind.Copy, [_root.Resolve("missing.txt")], folder)),
            service.Enqueue(new FileOperationRequest(FileOperationKind.Rename, [file], NewName: "bad:name.txt")),
            service.Enqueue(new FileOperationRequest(FileOperationKind.CreateFolder, [], folder, "CON")),
            service.Enqueue(new FileOperationRequest(FileOperationKind.Move, [file], _root.Resolve("nowhere"))),
            service.Enqueue(new FileOperationRequest(FileOperationKind.Delete, [Path.GetPathRoot(folder)!])),
        };

        // Assert
        results.ShouldAllBe(result => result.IsFailure && result.Error!.Code == FileOperationService.InvalidRequestCode);
        service.GetAll().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("report.pdf", true)]
    [InlineData("Отчёт за квартал (2).docx", true)]
    [InlineData("", false)]
    [InlineData("a/b", false)]
    [InlineData("name.", false)]
    [InlineData(" lead", false)]
    [InlineData("nul.txt", false)]
    [InlineData("..", false)]
    public void NameProblem_ChecksWindowsRules(string name, bool valid)
        => (FileOperationService.NameProblem(name) is null).ShouldBe(valid);

    [Fact]
    public async Task Queue_RunsInOrderAndListsNewestFirst()
    {
        // Arrange
        var a = _root.CreateFile(Path.Combine("q", "a.txt"), "a");
        var destination = _root.CreateDirectory("q-dest");
        var service = Service();

        // Act
        var first = service.Enqueue(new FileOperationRequest(FileOperationKind.Copy, [a], destination)).Value;
        var second = service.Enqueue(new FileOperationRequest(FileOperationKind.Rename, [a], NewName: "b.txt")).Value;
        await service.WhenIdleAsync(TestContext.Current.CancellationToken).WaitAsync(Wait, TestContext.Current.CancellationToken);
        var all = service.GetAll();

        // Assert
        all.Select(static info => info.Id).ShouldBe([second.Id, first.Id]);
        all.ShouldAllBe(info => info.State == OperationState.Completed);
        File.Exists(Path.Combine(destination, "a.txt")).ShouldBeTrue("копия сделана до переименования");
        service.GetActive().ShouldBeEmpty();
    }

    public async ValueTask DisposeAsync()
    {
        if (_service is not null)
        {
            await _service.DisposeAsync();
        }

        foreach (var file in Directory.EnumerateFiles(_root.FullPath, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        _root.Dispose();
    }

    [Theory]
    [InlineData("Microsoft Excel", "«Пароли.csv» открыт в программе Microsoft Excel — закройте его там и повторите.")]
    [InlineData(null, "«Пароли.csv» открыт в другой программе — закройте его там и повторите.")]
    public async Task Move_FileOpenInAnotherProgram_FailsSayingWhichToClose(string? application, string expected)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Занятый файл не переносится только в Windows.");

        // Arrange: файл держит открытым другая программа — как Excel держал «Пароли Brave.csv» (замечание 2026-10-12)
        var file = _root.CreateFile(Path.Combine("src", "Пароли.csv"), "сайт;логин");
        var destination = _root.CreateDirectory("dest");
        await using var service = new FileOperationService(new System.IO.Abstractions.FileSystem(), _events, SystemClock.Instance, PathNormalizer.Current,
            locks: new FixedLocks(application));
        using var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);

        // Act
        var info = await RunAsync(service, new FileOperationRequest(FileOperationKind.Move, [file], destination));

        // Assert
        info.State.ShouldBe(OperationState.Failed);
        info.Message.ShouldNotBeNull().ShouldContain(expected);
        File.Exists(file).ShouldBeTrue();
        File.Exists(Path.Combine(destination, "Пароли.csv")).ShouldBeFalse();
    }

    private FileOperationService Service(FileOperationOptions? options = null)
        => _service ??= new FileOperationService(new System.IO.Abstractions.FileSystem(), _events, SystemClock.Instance, PathNormalizer.Current, options, _shell, _sink);

    private static async Task<FileOperationInfo> RunAsync(FileOperationService service, FileOperationRequest request)
    {
        var queued = service.Enqueue(request);
        queued.IsSuccess.ShouldBeTrue(queued.Error?.Message);
        await service.WhenIdleAsync(TestContext.Current.CancellationToken).WaitAsync(Wait, TestContext.Current.CancellationToken);
        return service.Get(queued.Value.Id)!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        condition().ShouldBeTrue("условие не выполнилось за отведённое время");
    }

    private sealed class FixedLocks(string? application) : IFileLockInspector
    {
        public IReadOnlyList<string> FindLockingApplications(string path) => application is null ? [] : [application];
    }

    private sealed class RecordingSink : IPathChangeSink
    {
        private readonly ConcurrentQueue<string> _paths = new();

        public IReadOnlyCollection<string> Paths => _paths;

        public void OnPathsChanged(IReadOnlyCollection<string> absolutePaths)
        {
            foreach (var path in absolutePaths)
            {
                _paths.Enqueue(path);
            }
        }
    }

    /// <summary>
    /// Корзина для тестов: переносит файлы во временную папку «_trash» и запоминает исходные пути.
    /// </summary>
    private sealed class RecycleShell(string trash) : IPlatformShell
    {
        private readonly List<string> _recycled = [];

        public IReadOnlyList<string> Recycled
        {
            get
            {
                lock (_recycled)
                {
                    return [.. _recycled];
                }
            }
        }

        public Task<Result> MoveToRecycleBinAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        {
            foreach (var path in paths)
            {
                var target = Path.Combine(trash, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(path));
                if (Directory.Exists(path))
                {
                    Directory.Move(path, target);
                }
                else
                {
                    File.Move(path, target);
                }

                lock (_recycled)
                {
                    _recycled.Add(path);
                }
            }

            return Task.FromResult(Result.Ok());
        }

        public Task<Result> OpenAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(Result.Ok());

        public Task<Result> OpenWithAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(Result.Ok());

        public Task<Result> RevealInExplorerAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(Result.Ok());

        public Task<Result> ShowPropertiesAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(Result.Ok());

        public Task<Result> RunAsAdminAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(Result.Ok());

        public Task<Result> CopyPathsToClipboardAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) => Task.FromResult(Result.Ok());

        public Task<Result> CopyFilesToClipboardAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) => Task.FromResult(Result.Ok());
    }
}

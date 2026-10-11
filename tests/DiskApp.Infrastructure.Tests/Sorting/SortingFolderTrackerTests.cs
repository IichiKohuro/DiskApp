using DiskApp.Core.Abstractions;
using DiskApp.Core.Events;
using DiskApp.Core.Operations;
using DiskApp.Core.Paths;
using DiskApp.Core.Settings;
using DiskApp.Core.Sorting;

namespace DiskApp.Infrastructure.Tests.Sorting;

/// <summary>
/// Пути папок разбора следуют за переименованием (PLAN.md, этап 17.2): переименование папки, увиденное наблюдателем, и
/// перенос через очередь операций меняют пути накопителей, рабочих папок и основной папки-накопителя в настройках.
/// </summary>
public sealed class SortingFolderTrackerTests : IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private readonly TemporaryDirectory _root = new("tracker");
    private readonly IndexEventBus _events = new();
    private readonly MemorySettingsStore _store = new();
    private FileOperationService? _operations;

    [Fact]
    public void Remap_FolderAndNested_FollowRenameKeepingLabels()
    {
        // Arrange
        var sorting = new SortingSettings
        {
            Inboxes = [new SortingFolder { Path = @"D:\Загрузки" }],
            WorkFolders =
            [
                new SortingFolder { Path = @"D:\Работа\417", Label = "Проект" },
                new SortingFolder { Path = @"D:\Работа\417\Переписка" },
                new SortingFolder { Path = @"D:\Работа\4170" },
            ],
            MainInbox = @"D:\Работа\417\Входящие",
        };

        // Act
        var remapped = SortingFolderTracker.Remap(sorting, @"D:\Работа\417", @"D:\Работа\417 ТЗ", PathNormalizer.Windows);

        // Assert
        remapped.WorkFolders.Select(static folder => folder.Path).ShouldBe([@"D:\Работа\417 ТЗ", @"D:\Работа\417 ТЗ\Переписка", @"D:\Работа\4170"]);
        remapped.WorkFolders[0].Label.ShouldBe("Проект");
        remapped.Inboxes.ShouldHaveSingleItem().Path.ShouldBe(@"D:\Загрузки");
        remapped.MainInbox.ShouldBe(@"D:\Работа\417 ТЗ\Входящие");
    }

    [Fact]
    public void Remap_Unrelated_ReturnsSameSettings()
    {
        // Arrange
        var sorting = new SortingSettings { WorkFolders = [new SortingFolder { Path = @"D:\Работа\417" }] };

        // Act
        var remapped = SortingFolderTracker.Remap(sorting, @"D:\Фото", @"D:\Фото 2026", PathNormalizer.Windows);

        // Assert
        remapped.ShouldBeSameAs(sorting, "без изменений настройки не перезаписываются");
    }

    [Fact]
    public async Task RenamedFolderEvent_UpdatesSavedSettings_FileRenameIgnored()
    {
        // Arrange
        var old = _root.CreateDirectory("Работа");
        var renamed = _root.Resolve("Работа 2026");
        await _store.SaveAsync(new AppSettings { Sorting = new SortingSettings { WorkFolders = [new SortingFolder { Path = old }] } }, TestContext.Current.CancellationToken);
        using var tracker = Tracker();

        // Act
        _events.Publish(new EntryRenamedEvent(1, Path.Combine(old, "a.txt"), Path.Combine(old, "b.txt"), IsDirectory: false));
        await tracker.WhenIdleAsync().WaitAsync(Wait, TestContext.Current.CancellationToken);
        var afterFile = _store.SaveCount;
        _events.Publish(new EntryRenamedEvent(1, old, renamed, IsDirectory: true));
        await tracker.WhenIdleAsync().WaitAsync(Wait, TestContext.Current.CancellationToken);

        // Assert
        afterFile.ShouldBe(1, "переименование файла папки разбора не трогает");
        _store.Current.Sorting.WorkFolders.ShouldHaveSingleItem().Path.ShouldBe(renamed);
    }

    [Fact]
    public async Task MoveThroughOperations_UpdatesWorkFolderAndInbox()
    {
        // Arrange: рабочая папка и накопитель переезжают операцией DiskApp
        var work = _root.CreateDirectory(Path.Combine("Работа", "417"));
        var inbox = _root.CreateDirectory("Загрузки");
        var archive = _root.CreateDirectory("Архив");
        await _store.SaveAsync(new AppSettings
        {
            Sorting = new SortingSettings
            {
                Inboxes = [new SortingFolder { Path = inbox }],
                WorkFolders = [new SortingFolder { Path = work }],
                MainInbox = inbox,
            },
        }, TestContext.Current.CancellationToken);
        using var tracker = Tracker();
        var operations = Operations();

        // Act
        operations.Enqueue(new FileOperationRequest(FileOperationKind.Move, [work, inbox], archive)).IsSuccess.ShouldBeTrue();
        await operations.WhenIdleAsync(TestContext.Current.CancellationToken).WaitAsync(Wait, TestContext.Current.CancellationToken);
        await tracker.WhenIdleAsync().WaitAsync(Wait, TestContext.Current.CancellationToken);

        // Assert
        var sorting = _store.Current.Sorting;
        sorting.WorkFolders.ShouldHaveSingleItem().Path.ShouldBe(Path.Combine(archive, "417"));
        sorting.Inboxes.ShouldHaveSingleItem().Path.ShouldBe(Path.Combine(archive, "Загрузки"));
        sorting.MainInbox.ShouldBe(Path.Combine(archive, "Загрузки"));
    }

    public async ValueTask DisposeAsync()
    {
        if (_operations is not null)
        {
            await _operations.DisposeAsync();
        }

        _root.Dispose();
    }

    private FileOperationService Operations()
        => _operations ??= new FileOperationService(new System.IO.Abstractions.FileSystem(), _events, SystemClock.Instance, PathNormalizer.Current);

    private SortingFolderTracker Tracker() => new(_events, Operations(), _store, PathNormalizer.Current);

    private sealed class MemorySettingsStore : ISettingsStore<AppSettings>
    {
        public AppSettings Current { get; private set; } = new();

        public int SaveCount { get; private set; }

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Current = settings;
            SaveCount++;
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

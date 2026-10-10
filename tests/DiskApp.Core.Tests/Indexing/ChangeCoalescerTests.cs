using DiskApp.Core.Indexing.Watching;
using DiskApp.Core.Paths;

namespace DiskApp.Core.Tests.Indexing;

/// <summary>
/// Склейка событий наблюдателя: срок готовности по паузе и по пределу задержки, сведение повторов к одному пути,
/// порядок переименований, переполнение, состояние корня во время сканирования.
/// </summary>
public sealed class ChangeCoalescerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 11, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DueAt_QuietAfterLastEventButCappedByMaxDelay()
    {
        // Arrange
        var coalescer = Create();

        // Act & Assert
        coalescer.DueAt.ShouldBeNull();
        coalescer.Add(new RawChange(RawChangeKind.Created, "a.txt"), T0);
        coalescer.DueAt.ShouldBe(T0.AddMilliseconds(400));
        coalescer.Add(new RawChange(RawChangeKind.Changed, "a.txt"), T0.AddMilliseconds(300));
        coalescer.DueAt.ShouldBe(T0.AddMilliseconds(700));
        coalescer.Add(new RawChange(RawChangeKind.Changed, "a.txt"), T0.AddMilliseconds(800));
        coalescer.DueAt.ShouldBe(T0.AddMilliseconds(1000), "не позже предела задержки от первого события");
        coalescer.IsDue(T0.AddMilliseconds(999)).ShouldBeFalse();
        coalescer.IsDue(T0.AddMilliseconds(1000)).ShouldBeTrue();
    }

    [Fact]
    public void Drain_RepeatedEvents_CollapseToDistinctPathsIgnoringCase()
    {
        // Arrange
        var coalescer = Create();
        coalescer.Add(new RawChange(RawChangeKind.Created, @"docs\a.txt"), T0);
        coalescer.Add(new RawChange(RawChangeKind.Changed, @"docs\a.txt"), T0);
        coalescer.Add(new RawChange(RawChangeKind.Changed, @"DOCS\A.TXT"), T0);
        coalescer.Add(new RawChange(RawChangeKind.Deleted, @"docs\b.txt"), T0);
        coalescer.Add(new RawChange(RawChangeKind.Changed, string.Empty), T0);

        // Act
        var batch = coalescer.Drain();

        // Assert
        batch.Paths.ShouldBe([@"docs\a.txt", @"docs\b.txt"], ignoreOrder: true);
        batch.Renames.ShouldBeEmpty();
        batch.Overflowed.ShouldBeFalse();
        batch.IsEmpty.ShouldBeFalse();
        coalescer.HasPending.ShouldBeFalse();
        coalescer.DueAt.ShouldBeNull();
    }

    [Fact]
    public void Drain_RenameChain_KeepsOrderAndChecksBothPaths()
    {
        // Arrange
        var coalescer = Create();
        coalescer.Add(new RawChange(RawChangeKind.Renamed, "b.txt", "a.txt"), T0);
        coalescer.Add(new RawChange(RawChangeKind.Renamed, "c.txt", "b.txt"), T0);

        // Act
        var batch = coalescer.Drain();

        // Assert
        batch.Renames.ShouldBe([("a.txt", "b.txt"), ("b.txt", "c.txt")]);
        batch.Paths.ShouldBe(["a.txt", "b.txt", "c.txt"], ignoreOrder: true);
    }

    [Fact]
    public void Add_TooManyPaths_OverflowsAndDropsDetails()
    {
        // Arrange
        var coalescer = new ChangeCoalescer(PathNormalizer.Windows, TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(1), maxPaths: 3);

        // Act
        for (var i = 0; i < 5; i++)
        {
            coalescer.Add(new RawChange(RawChangeKind.Created, $"f{i}.txt"), T0);
        }

        var batch = coalescer.Drain();

        // Assert
        batch.Overflowed.ShouldBeTrue();
        batch.Paths.ShouldBeEmpty();
        batch.IsEmpty.ShouldBeFalse();
        coalescer.Drain().IsEmpty.ShouldBeTrue("после выдачи переполнение сбрасывается");
    }

    [Fact]
    public void MarkOverflowed_PendingChanges_ReplacedByOverflow()
    {
        // Arrange
        var coalescer = Create();
        coalescer.Add(new RawChange(RawChangeKind.Created, "a.txt"), T0);

        // Act
        coalescer.MarkOverflowed();
        coalescer.Add(new RawChange(RawChangeKind.Created, "b.txt"), T0);
        var batch = coalescer.Drain();

        // Assert
        batch.Overflowed.ShouldBeTrue();
        batch.Paths.ShouldBeEmpty();
    }

    [Fact]
    public void RootIndexState_DuringScan_UsesActiveIdAndRecordsTouchedPaths()
    {
        // Arrange
        var state = new RootIndexState(PathNormalizer.Windows);

        // Act & Assert
        state.CurrentScanId.ShouldBe(0);
        state.RecordTouched("before.txt");
        state.BeginScan(7);
        state.IsScanActive.ShouldBeTrue();
        state.CurrentScanId.ShouldBe(7);
        state.RecordTouched("a.txt");
        state.RecordTouched("A.TXT");
        state.EndScan().ShouldBe(["a.txt"]);
        state.IsScanActive.ShouldBeFalse();
        state.CurrentScanId.ShouldBe(7, "после сканирования действует его номер");
        state.EndScan().ShouldBeEmpty();
    }

    private static ChangeCoalescer Create() => new(PathNormalizer.Windows, TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(1));
}

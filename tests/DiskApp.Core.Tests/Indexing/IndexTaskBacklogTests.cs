using DiskApp.Core.Indexing;
using DiskApp.Core.Paths;

namespace DiskApp.Core.Tests.Indexing;

/// <summary>
/// Очередь задач индексации: приоритеты, поглощение покрытых задач, выбор только запускаемых, удаление корня.
/// </summary>
public sealed class IndexTaskBacklogTests
{
    [Fact]
    public void TryDequeue_MixedTasks_SubtreeReconcileThenRootReconcileThenFullScan()
    {
        // Arrange
        var backlog = new IndexTaskBacklog(PathNormalizer.Windows);
        backlog.Enqueue(new IndexTask(1, IndexTaskKind.FullScan, string.Empty, "full"));
        backlog.Enqueue(new IndexTask(2, IndexTaskKind.Reconcile, string.Empty, "root"));
        backlog.Enqueue(new IndexTask(3, IndexTaskKind.Reconcile, "docs", "subtree"));

        // Act
        var order = new[] { backlog.TryDequeue(static _ => true), backlog.TryDequeue(static _ => true), backlog.TryDequeue(static _ => true) };

        // Assert
        order.Select(static task => task!.Reason).ShouldBe(["subtree", "root", "full"]);
        backlog.TryDequeue(static _ => true).ShouldBeNull();
    }

    [Fact]
    public void Enqueue_TaskCoveredByPending_IsRejected()
    {
        // Arrange
        var backlog = new IndexTaskBacklog(PathNormalizer.Windows);
        backlog.Enqueue(new IndexTask(1, IndexTaskKind.FullScan, string.Empty, "full"));

        // Act & Assert
        backlog.Enqueue(new IndexTask(1, IndexTaskKind.Reconcile, string.Empty, "reconcile")).ShouldBeFalse();
        backlog.Enqueue(new IndexTask(1, IndexTaskKind.Reconcile, @"docs\sub", "subtree")).ShouldBeFalse();
        backlog.Enqueue(new IndexTask(1, IndexTaskKind.FullScan, string.Empty, "again")).ShouldBeFalse();
        backlog.Enqueue(new IndexTask(2, IndexTaskKind.Reconcile, string.Empty, "other root")).ShouldBeTrue();
        backlog.Count.ShouldBe(2);
    }

    [Fact]
    public void Enqueue_WiderTask_RemovesCoveredPending()
    {
        // Arrange
        var backlog = new IndexTaskBacklog(PathNormalizer.Windows);
        backlog.Enqueue(new IndexTask(1, IndexTaskKind.Reconcile, @"docs\a", "a"));
        backlog.Enqueue(new IndexTask(1, IndexTaskKind.Reconcile, @"DOCS\b", "b"));
        backlog.Enqueue(new IndexTask(1, IndexTaskKind.Reconcile, "docsx", "sibling with prefix"));

        // Act
        var added = backlog.Enqueue(new IndexTask(1, IndexTaskKind.Reconcile, "docs", "parent"));

        // Assert
        added.ShouldBeTrue();
        backlog.Snapshot().Select(static task => task.Reason).ShouldBe(["sibling with prefix", "parent"]);
    }

    [Fact]
    public void Enqueue_ReconcileWhileFullScanPendingForOtherSubtree_BothKept()
    {
        // Arrange
        var backlog = new IndexTaskBacklog(PathNormalizer.Windows);
        backlog.Enqueue(new IndexTask(1, IndexTaskKind.FullScan, "photos", "full photos"));

        // Act
        var added = backlog.Enqueue(new IndexTask(1, IndexTaskKind.Reconcile, "docs", "reconcile docs"));

        // Assert
        added.ShouldBeTrue();
        backlog.Count.ShouldBe(2);
    }

    [Fact]
    public void TryDequeue_OnlyRunnable_SkipsBlocked()
    {
        // Arrange
        var backlog = new IndexTaskBacklog(PathNormalizer.Windows);
        backlog.Enqueue(new IndexTask(1, IndexTaskKind.Reconcile, "a", "busy root"));
        backlog.Enqueue(new IndexTask(2, IndexTaskKind.FullScan, string.Empty, "free root"));

        // Act
        var task = backlog.TryDequeue(static candidate => candidate.RootId != 1);

        // Assert
        task!.Reason.ShouldBe("free root");
        backlog.HasPendingFor(1).ShouldBeTrue();
        backlog.HasPendingFor(2).ShouldBeFalse();
    }

    [Fact]
    public void RemoveRoot_DropsAllItsTasks()
    {
        // Arrange
        var backlog = new IndexTaskBacklog(PathNormalizer.Windows);
        backlog.Enqueue(new IndexTask(1, IndexTaskKind.Reconcile, "a", "a"));
        backlog.Enqueue(new IndexTask(1, IndexTaskKind.Reconcile, "b", "b"));
        backlog.Enqueue(new IndexTask(2, IndexTaskKind.Reconcile, "c", "c"));

        // Act
        var removed = backlog.RemoveRoot(1);

        // Assert
        removed.ShouldBe(2);
        backlog.Snapshot().ShouldHaveSingleItem().RootId.ShouldBe(2);
    }

    [Theory]
    [InlineData(IndexTaskKind.Reconcile, "docs", 0)]
    [InlineData(IndexTaskKind.Reconcile, "", 1)]
    [InlineData(IndexTaskKind.FullScan, "", 2)]
    [InlineData(IndexTaskKind.FullScan, "docs", 2)]
    public void Priority_ByKindAndScope(IndexTaskKind kind, string relPath, int expected)
    {
        // Act & Assert
        new IndexTask(1, kind, relPath, string.Empty).Priority.ShouldBe(expected);
    }
}

using DiskApp.Core.Domain;

using static DiskApp.Infrastructure.Tests.Sqlite.Repositories.IndexTestContext;

namespace DiskApp.Infrastructure.Tests.Sqlite.Repositories;

/// <summary>
/// Теги и коллекции: уникальность имени без учёта регистра (включая кириллицу), привязки, порядок элементов, каскады.
/// </summary>
public sealed class SqliteTagAndCollectionRepositoryTests
{
    [Fact]
    public async Task Tags_AddSameNameDifferentCase_ReturnsExistingTag()
    {
        // Arrange
        await using var context = await CreateAsync();

        // Act
        var first = await context.Tags.AddAsync("Работа", "#ff0000", TestContext.Current.CancellationToken);
        var second = await context.Tags.AddAsync("  работа ", null, TestContext.Current.CancellationToken);
        var byName = await context.Tags.GetByNameAsync("РАБОТА", TestContext.Current.CancellationToken);

        // Assert
        first.Id.ShouldBeGreaterThan(0);
        first.Name.ShouldBe("Работа");
        second.ShouldBe(first);
        byName.ShouldBe(first);
        (await context.Tags.GetAllAsync(TestContext.Current.CancellationToken)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Tags_UpdateAndDelete_PersistAndCascadeAssignments()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.UpsertAsync(1, context.Entry("a.txt"));
        var entry = await context.GetAsync("a.txt");
        var tag = await context.Tags.AddAsync("draft", null, TestContext.Current.CancellationToken);
        await context.Tags.AssignAsync(entry.Id, tag.Id, TestContext.Current.CancellationToken);

        // Act
        var updated = await context.Tags.UpdateAsync(tag with { Name = "Черновик", Color = "#00ff00" }, TestContext.Current.CancellationToken);
        var renamed = await context.Tags.GetByIdAsync(tag.Id, TestContext.Current.CancellationToken);
        var deleted = await context.Tags.DeleteAsync(tag.Id, TestContext.Current.CancellationToken);

        // Assert
        updated.ShouldBeTrue();
        renamed.ShouldBe(new Tag(tag.Id, "Черновик", "#00ff00"));
        deleted.ShouldBeTrue();
        (await context.Tags.DeleteAsync(tag.Id, TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await context.Tags.GetTagsOfEntryAsync(entry.Id, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Tags_AssignUnassignAndQuery_WorkPerEntryAndInBulk()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.UpsertAsync(1, context.Entry("a.txt"), context.Entry("b.txt"), context.Entry("c.txt"));
        var a = await context.GetAsync("a.txt");
        var b = await context.GetAsync("b.txt");
        var c = await context.GetAsync("c.txt");
        var work = await context.Tags.AddAsync("work", null, TestContext.Current.CancellationToken);
        var urgent = await context.Tags.AddAsync("Urgent", null, TestContext.Current.CancellationToken);

        // Act
        await context.Tags.AssignAsync(a.Id, work.Id, TestContext.Current.CancellationToken);
        await context.Tags.AssignAsync(a.Id, work.Id, TestContext.Current.CancellationToken);
        await context.Tags.AssignAsync(a.Id, urgent.Id, TestContext.Current.CancellationToken);
        await context.Tags.AssignAsync(b.Id, work.Id, TestContext.Current.CancellationToken);
        var unassigned = await context.Tags.UnassignAsync(b.Id, work.Id, TestContext.Current.CancellationToken);
        var unassignedAgain = await context.Tags.UnassignAsync(b.Id, work.Id, TestContext.Current.CancellationToken);
        var ofA = await context.Tags.GetTagsOfEntryAsync(a.Id, TestContext.Current.CancellationToken);
        var bulk = await context.Tags.GetTagsOfEntriesAsync([a.Id, b.Id, c.Id], TestContext.Current.CancellationToken);
        var withWork = await context.Tags.GetEntryIdsWithTagAsync(work.Id, TestContext.Current.CancellationToken);

        // Assert
        unassigned.ShouldBeTrue();
        unassignedAgain.ShouldBeFalse();
        ofA.Select(static tag => tag.Name).ShouldBe(["Urgent", "work"]);
        bulk.Keys.ShouldBe([a.Id]);
        bulk[a.Id].Count.ShouldBe(2);
        withWork.ShouldBe([a.Id]);
        (await context.Tags.GetTagsOfEntriesAsync([], TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Collections_Crud_RoundTrips()
    {
        // Arrange
        await using var context = await CreateAsync();

        // Act
        var added = await context.Collections.AddAsync(new Collection(0, "Отчёты", CollectionKind.SavedQuery, "ext:pdf"), TestContext.Current.CancellationToken);
        var updated = await context.Collections.UpdateAsync(added with { Name = "Все отчёты", Query = "ext:pdf modified:year" }, TestContext.Current.CancellationToken);
        var loaded = await context.Collections.GetByIdAsync(added.Id, TestContext.Current.CancellationToken);
        var all = await context.Collections.GetAllAsync(TestContext.Current.CancellationToken);
        var deleted = await context.Collections.DeleteAsync(added.Id, TestContext.Current.CancellationToken);

        // Assert
        added.Id.ShouldBeGreaterThan(0);
        updated.ShouldBeTrue();
        loaded.ShouldBe(new Collection(added.Id, "Все отчёты", CollectionKind.SavedQuery, "ext:pdf modified:year"));
        all.ShouldHaveSingleItem();
        deleted.ShouldBeTrue();
        (await context.Collections.GetByIdAsync(added.Id, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Collections_Items_AddRemoveReorderAndCascade()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.UpsertAsync(1, context.Entry("a.txt"), context.Entry("b.txt"), context.Entry("c.txt"));
        var a = await context.GetAsync("a.txt");
        var b = await context.GetAsync("b.txt");
        var c = await context.GetAsync("c.txt");
        var collection = await context.Collections.AddAsync(new Collection(0, "Избранные документы", CollectionKind.Manual), TestContext.Current.CancellationToken);

        // Act
        await context.Collections.AddItemAsync(collection.Id, a.Id, TestContext.Current.CancellationToken);
        await context.Collections.AddItemAsync(collection.Id, b.Id, TestContext.Current.CancellationToken);
        await context.Collections.AddItemAsync(collection.Id, c.Id, TestContext.Current.CancellationToken);
        await context.Collections.AddItemAsync(collection.Id, a.Id, TestContext.Current.CancellationToken);
        var initial = await context.Collections.GetItemsAsync(collection.Id, TestContext.Current.CancellationToken);
        await context.Collections.ReorderItemsAsync(collection.Id, [c.Id], TestContext.Current.CancellationToken);
        var reordered = await context.Collections.GetItemsAsync(collection.Id, TestContext.Current.CancellationToken);
        var removed = await context.Collections.RemoveItemAsync(collection.Id, b.Id, TestContext.Current.CancellationToken);
        await context.Entries.DeleteSubtreeAsync(context.Root.Id, "a.txt", TestContext.Current.CancellationToken);
        var afterCascade = await context.Collections.GetItemsAsync(collection.Id, TestContext.Current.CancellationToken);

        // Assert
        initial.ShouldBe([a.Id, b.Id, c.Id]);
        reordered.ShouldBe([c.Id, a.Id, b.Id]);
        removed.ShouldBeTrue();
        afterCascade.ShouldBe([c.Id]);
    }
}

using DiskApp.Core.Domain;

using static DiskApp.Infrastructure.Tests.Sqlite.Repositories.IndexTestContext;

namespace DiskApp.Infrastructure.Tests.Sqlite.Repositories;

/// <summary>
/// Статистика использования и избранное: инкремент открытий, недавние, порядок закрепления и каскад при удалении записи.
/// </summary>
public sealed class SqliteUsageAndFavoriteRepositoryTests
{
    [Fact]
    public async Task RecordOpenAsync_Twice_IncrementsCountAndKeepsLatestTime()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.UpsertAsync(1, context.Entry("a.txt"));
        var entry = await context.GetAsync("a.txt");

        // Act
        await context.Usage.RecordOpenAsync(entry.Id, Now.AddMinutes(-5), TestContext.Current.CancellationToken);
        await context.Usage.RecordOpenAsync(entry.Id, Now, TestContext.Current.CancellationToken);
        var stat = await context.Usage.GetAsync(entry.Id, TestContext.Current.CancellationToken);

        // Assert
        stat.ShouldBe(new UsageStat(entry.Id, 2, Now));
        (await context.Usage.GetAsync(999_999, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task GetRecentAndGetMany_SeveralEntries_OrderAndFilter()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.UpsertAsync(1, context.Entry("a.txt"), context.Entry("b.txt"), context.Entry("c.txt"));
        var a = await context.GetAsync("a.txt");
        var b = await context.GetAsync("b.txt");
        var c = await context.GetAsync("c.txt");
        await context.Usage.RecordOpenAsync(a.Id, Now.AddDays(-2), TestContext.Current.CancellationToken);
        await context.Usage.RecordOpenAsync(b.Id, Now, TestContext.Current.CancellationToken);
        await context.Usage.RecordOpenAsync(c.Id, Now.AddDays(-1), TestContext.Current.CancellationToken);

        // Act
        var recent = await context.Usage.GetRecentAsync(2, TestContext.Current.CancellationToken);
        var many = await context.Usage.GetManyAsync([a.Id, c.Id, 999_999], TestContext.Current.CancellationToken);

        // Assert
        recent.Select(static stat => stat.EntryId).ShouldBe([b.Id, c.Id]);
        many.Select(static stat => stat.EntryId).ShouldBe([a.Id, c.Id], ignoreOrder: true);
        (await context.Usage.GetManyAsync([], TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task RemoveAndCascade_UsageRow_DisappearsWithEntry()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.UpsertAsync(1, context.Entry("a.txt"), context.Entry("b.txt"));
        var a = await context.GetAsync("a.txt");
        var b = await context.GetAsync("b.txt");
        await context.Usage.RecordOpenAsync(a.Id, Now, TestContext.Current.CancellationToken);
        await context.Usage.RecordOpenAsync(b.Id, Now, TestContext.Current.CancellationToken);

        // Act
        await context.Usage.RemoveAsync(a.Id, TestContext.Current.CancellationToken);
        await context.Entries.DeleteSubtreeAsync(context.Root.Id, "b.txt", TestContext.Current.CancellationToken);

        // Assert
        (await context.Usage.GetAsync(a.Id, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await context.Usage.GetAsync(b.Id, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task Favorites_AddContainsRemove_KeepOrderOfPinning()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.UpsertAsync(1, context.Entry("a.txt"), context.Entry("b.txt"));
        var a = await context.GetAsync("a.txt");
        var b = await context.GetAsync("b.txt");

        // Act
        var first = await context.Favorites.AddAsync(b.Id, Now, TestContext.Current.CancellationToken);
        var second = await context.Favorites.AddAsync(a.Id, Now.AddMinutes(1), TestContext.Current.CancellationToken);
        var again = await context.Favorites.AddAsync(b.Id, Now.AddMinutes(2), TestContext.Current.CancellationToken);
        var all = await context.Favorites.GetAllAsync(TestContext.Current.CancellationToken);

        // Assert
        first.ShouldBe(new Favorite(b.Id, 0, Now));
        second.ShouldBe(new Favorite(a.Id, 1, Now.AddMinutes(1)));
        again.ShouldBe(first, "повторное закрепление ничего не меняет");
        all.Select(static favorite => favorite.EntryId).ShouldBe([b.Id, a.Id]);
        (await context.Favorites.ContainsAsync(a.Id, TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await context.Favorites.RemoveAsync(a.Id, TestContext.Current.CancellationToken)).ShouldBeTrue();
        (await context.Favorites.RemoveAsync(a.Id, TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await context.Favorites.ContainsAsync(a.Id, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task Favorites_ReorderAsync_PartialListMovesListedFirst()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.UpsertAsync(1, context.Entry("a.txt"), context.Entry("b.txt"), context.Entry("c.txt"));
        var ids = new List<long>();
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" })
        {
            var entry = await context.GetAsync(name);
            ids.Add(entry.Id);
            await context.Favorites.AddAsync(entry.Id, Now, TestContext.Current.CancellationToken);
        }

        // Act
        await context.Favorites.ReorderAsync([ids[2], 999_999, ids[2]], TestContext.Current.CancellationToken);
        var all = await context.Favorites.GetAllAsync(TestContext.Current.CancellationToken);

        // Assert
        all.Select(static favorite => favorite.EntryId).ShouldBe([ids[2], ids[0], ids[1]]);
        all.Select(static favorite => favorite.Order).ShouldBe([0, 1, 2]);
    }

    [Fact]
    public async Task Favorites_EntryDeleted_RowDisappears()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.UpsertAsync(1, context.Entry("a.txt"));
        var a = await context.GetAsync("a.txt");
        await context.Favorites.AddAsync(a.Id, Now, TestContext.Current.CancellationToken);

        // Act
        await context.Entries.DeleteByRootAsync(context.Root.Id, TestContext.Current.CancellationToken);

        // Assert
        (await context.Favorites.GetAllAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }
}

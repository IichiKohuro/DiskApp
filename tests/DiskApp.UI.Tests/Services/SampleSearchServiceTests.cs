using DiskApp.UI.Services;

namespace DiskApp.UI.Tests.Services;

/// <summary>
/// Макетный поиск: ранжирование по началу имени, избранному и свежести; недавние с учётом открытий.
/// </summary>
public sealed class SampleSearchServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 15, 0, 0, TimeSpan.FromHours(3));

    [Fact]
    public async Task SearchAsync_PrefixMatch_RanksAboveSubstringMatch()
    {
        // Arrange
        var service = new SampleSearchService(static () => Now);

        // Act
        var result = await service.SearchAsync("read", TestContext.Current.CancellationToken);

        // Assert
        result.Entries[0].Name.ShouldBe("README.md");
        result.TotalCount.ShouldBe(result.Entries.Count);
        result.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public async Task SearchAsync_EmptyQuery_ReturnsEmptyResult()
    {
        // Arrange
        var service = new SampleSearchService(static () => Now);

        // Act
        var result = await service.SearchAsync("   ", TestContext.Current.CancellationToken);

        // Assert
        result.Entries.ShouldBeEmpty();
        result.TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task SearchAsync_WordInDirectoryOnly_StillMatchesWithLowerScore()
    {
        // Arrange
        var service = new SampleSearchService(static () => Now);

        // Act
        var result = await service.SearchAsync("Крым", TestContext.Current.CancellationToken);

        // Assert
        result.Entries.Count.ShouldBe(3);
        result.Entries.ShouldAllBe(entry => entry.Directory.Contains("Крым", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecordOpenedAsync_ExistingEntry_MovesItToFrontOfRecent()
    {
        // Arrange
        var service = new SampleSearchService(static () => Now);
        var before = await service.GetRecentAsync(TestContext.Current.CancellationToken);
        var target = before[^1];

        // Act
        await service.RecordOpenedAsync(target, TestContext.Current.CancellationToken);
        var after = await service.GetRecentAsync(TestContext.Current.CancellationToken);

        // Assert
        after[0].FullPath.ShouldBe(target.FullPath);
        after.Count.ShouldBe(before.Count);
    }

    [Fact]
    public async Task GetIndexStatusAsync_Always_DescribesSampleIndex()
    {
        // Arrange
        var service = new SampleSearchService(static () => Now);

        // Act
        var status = await service.GetIndexStatusAsync(TestContext.Current.CancellationToken);

        // Assert
        status.TotalEntries.ShouldBe(128_412);
        status.Roots.Count.ShouldBe(4);
        status.InboxCount.ShouldBe(12);
        status.Describe(Now).ShouldBe("128 412 файлов в 4 корнях · обновлён 2 минуты назад");
    }
}

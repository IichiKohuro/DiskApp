using DiskApp.Core.Repositories;

using static DiskApp.Infrastructure.Tests.Sqlite.Repositories.IndexTestContext;

namespace DiskApp.Infrastructure.Tests.Sqlite.Repositories;

/// <summary>
/// История запросов: счётчик и свежесть, порядок недавних, подсказки по префиксу без учёта регистра, удаление и очистка.
/// </summary>
public sealed class SqliteQueryHistoryRepositoryTests
{
    [Fact]
    public async Task RecordAsync_SameQueryTwice_IncrementsAndRefreshes()
    {
        // Arrange
        await using var context = await CreateAsync();

        // Act
        await context.History.RecordAsync("отчёт ext:pdf", Now.AddMinutes(-1), TestContext.Current.CancellationToken);
        await context.History.RecordAsync("  отчёт ext:pdf ", Now, TestContext.Current.CancellationToken);
        var recent = await context.History.GetRecentAsync(10, TestContext.Current.CancellationToken);

        // Assert
        recent.ShouldHaveSingleItem().ShouldBe(new QueryHistoryEntry("отчёт ext:pdf", 2, Now));
    }

    [Fact]
    public async Task GetRecentAndSuggest_SeveralQueries_OrderByFreshnessAndMatchPrefixIgnoringCase()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.History.RecordAsync("Отчёт 2025", Now.AddDays(-2), TestContext.Current.CancellationToken);
        await context.History.RecordAsync("отчёт 2026", Now, TestContext.Current.CancellationToken);
        await context.History.RecordAsync("invoice", Now.AddDays(-1), TestContext.Current.CancellationToken);

        // Act
        var recent = await context.History.GetRecentAsync(2, TestContext.Current.CancellationToken);
        var suggestions = await context.History.SuggestAsync("ОТЧ", 10, TestContext.Current.CancellationToken);
        var limited = await context.History.SuggestAsync("отч", 1, TestContext.Current.CancellationToken);

        // Assert
        recent.Select(static entry => entry.Query).ShouldBe(["отчёт 2026", "invoice"]);
        suggestions.Select(static entry => entry.Query).ShouldBe(["отчёт 2026", "Отчёт 2025"]);
        limited.Select(static entry => entry.Query).ShouldBe(["отчёт 2026"]);
    }

    [Fact]
    public async Task RemoveAndClear_History_Empties()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.History.RecordAsync("a", Now, TestContext.Current.CancellationToken);
        await context.History.RecordAsync("b", Now, TestContext.Current.CancellationToken);

        // Act
        var removed = await context.History.RemoveAsync("a", TestContext.Current.CancellationToken);
        var removedAgain = await context.History.RemoveAsync("a", TestContext.Current.CancellationToken);
        await context.History.ClearAsync(TestContext.Current.CancellationToken);

        // Assert
        removed.ShouldBeTrue();
        removedAgain.ShouldBeFalse();
        (await context.History.GetRecentAsync(10, TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }
}

using DiskApp.Core.Paths;
using DiskApp.Core.Sorting;
using DiskApp.Infrastructure.Sqlite;
using DiskApp.Infrastructure.Sqlite.Repositories;

namespace DiskApp.Infrastructure.Tests.Sqlite.Repositories;

/// <summary>
/// Состояния файлов накопителей (PLAN.md, этап 17.1): переживают перезапуск, «новый» удаляет запись, путь в другом регистре
/// в Windows — тот же файл.
/// </summary>
public sealed class SqliteInboxStateRepositoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task States_SurviveReopen_NewRemovesRow()
    {
        // Arrange
        using var folder = new TemporaryDirectory("inbox-states");
        var later = folder.Resolve("потом.pdf");
        var keep = folder.Resolve("оставить.exe");
        await using (var database = await OpenAsync(folder))
        {
            var states = new SqliteInboxStateRepository(database, PathNormalizer.Current);
            await states.SetAsync([later], InboxItemState.Postponed, Now, TestContext.Current.CancellationToken);
            await states.SetAsync([keep], InboxItemState.Kept, Now, TestContext.Current.CancellationToken);
            await states.SetAsync([keep], InboxItemState.Postponed, Now, TestContext.Current.CancellationToken);
        }

        // Act: база открыта заново — как после перезапуска приложения
        await using var reopened = await OpenAsync(folder);
        var repository = new SqliteInboxStateRepository(reopened, PathNormalizer.Current);
        var restored = await repository.GetAllAsync(TestContext.Current.CancellationToken);
        await repository.SetAsync([later], InboxItemState.New, Now, TestContext.Current.CancellationToken);
        await repository.RemoveAsync([keep], TestContext.Current.CancellationToken);
        var cleared = await repository.GetAllAsync(TestContext.Current.CancellationToken);

        // Assert
        restored.Count.ShouldBe(2);
        restored[later].ShouldBe(InboxItemState.Postponed);
        restored[keep].ShouldBe(InboxItemState.Postponed);
        cleared.ShouldBeEmpty();
    }

    [Fact]
    public async Task Set_SamePathInOtherCase_ReplacesOnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Регистр путей не важен только в Windows.");

        // Arrange
        using var folder = new TemporaryDirectory("inbox-states");
        await using var database = await OpenAsync(folder);
        var states = new SqliteInboxStateRepository(database, PathNormalizer.Current);
        var path = folder.Resolve("Договор.docx");

        // Act
        await states.SetAsync([path], InboxItemState.Kept, Now, TestContext.Current.CancellationToken);
        await states.SetAsync([path.ToUpperInvariant()], InboxItemState.Postponed, Now, TestContext.Current.CancellationToken);

        // Assert
        var all = await states.GetAllAsync(TestContext.Current.CancellationToken);
        all.ShouldHaveSingleItem().Value.ShouldBe(InboxItemState.Postponed);
        all[path].ShouldBe(InboxItemState.Postponed);
    }

    private static async Task<SqliteDatabase> OpenAsync(TemporaryDirectory folder)
    {
        var database = new SqliteDatabase(folder.Resolve("index.db"));
        await database.InitializeAsync(TestContext.Current.CancellationToken);
        return database;
    }
}

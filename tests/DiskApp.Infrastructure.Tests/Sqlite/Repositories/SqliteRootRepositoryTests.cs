using DiskApp.Core.Domain;

using static DiskApp.Infrastructure.Tests.Sqlite.Repositories.IndexTestContext;

namespace DiskApp.Infrastructure.Tests.Sqlite.Repositories;

/// <summary>
/// Корни: добавление с идентификатором, чтение, обновление, состояние и время сканирования, каскадное удаление записей.
/// </summary>
public sealed class SqliteRootRepositoryTests
{
    [Fact]
    public async Task AddAsync_NewRoot_AssignsIdAndRoundTripsAllFields()
    {
        // Arrange
        await using var context = await CreateAsync();
        var root = new Root
        {
            Path = OperatingSystem.IsWindows() ? @"E:\Photos" : "/photos",
            Label = "Фото",
            IsEnabled = false,
            IncludeHidden = true,
            IncludeSystem = true,
            MaxDepth = 3,
            LastScanUtc = Now.AddHours(-2),
            Status = RootStatus.Paused,
            ErrorMessage = null,
        };

        // Act
        var added = await context.Roots.AddAsync(root, TestContext.Current.CancellationToken);
        var all = await context.Roots.GetAllAsync(TestContext.Current.CancellationToken);
        var loaded = await context.Roots.GetByIdAsync(added.Id, TestContext.Current.CancellationToken);

        // Assert
        added.Id.ShouldBeGreaterThan(context.Root.Id);
        added.ShouldBe(root with { Id = added.Id });
        all.Select(static r => r.Id).ShouldBe([context.Root.Id, added.Id]);
        loaded.ShouldBe(added);
    }

    [Fact]
    public async Task UpdateSetStatusSetLastScan_ExistingRoot_PersistChanges()
    {
        // Arrange
        await using var context = await CreateAsync();

        // Act
        var updated = await context.Roots.UpdateAsync(context.Root with { Label = "Проекты", MaxDepth = 5 }, TestContext.Current.CancellationToken);
        await context.Roots.SetStatusAsync(context.Root.Id, RootStatus.Error, "диск извлечён", TestContext.Current.CancellationToken);
        await context.Roots.SetLastScanAsync(context.Root.Id, Now, TestContext.Current.CancellationToken);
        var loaded = await context.Roots.GetByIdAsync(context.Root.Id, TestContext.Current.CancellationToken);

        // Assert
        updated.ShouldBeTrue();
        loaded.ShouldNotBeNull();
        loaded.Label.ShouldBe("Проекты");
        loaded.MaxDepth.ShouldBe(5);
        loaded.Status.ShouldBe(RootStatus.Error);
        loaded.ErrorMessage.ShouldBe("диск извлечён");
        loaded.LastScanUtc.ShouldBe(Now);
    }

    [Fact]
    public async Task UpdateAndDelete_MissingRoot_ReturnFalse()
    {
        // Arrange
        await using var context = await CreateAsync();

        // Act & Assert
        (await context.Roots.UpdateAsync(context.Root with { Id = 12_345 }, TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await context.Roots.DeleteAsync(12_345, TestContext.Current.CancellationToken)).ShouldBeFalse();
        (await context.Roots.GetByIdAsync(12_345, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAsync_RootWithEntries_CascadesToEntries()
    {
        // Arrange
        await using var context = await CreateAsync();
        await context.UpsertAsync(1, context.Entry("a", isDirectory: true), context.Entry("a/b.txt"));

        // Act
        var deleted = await context.Roots.DeleteAsync(context.Root.Id, TestContext.Current.CancellationToken);

        // Assert
        deleted.ShouldBeTrue();
        (await context.Entries.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        (await context.Roots.GetAllAsync(TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AddAsync_DuplicatePath_Throws()
    {
        // Arrange
        await using var context = await CreateAsync();

        // Act & Assert
        await Should.ThrowAsync<Microsoft.Data.Sqlite.SqliteException>(
            () => context.Roots.AddAsync(new Root { Path = context.Root.Path, Label = "Снова" }, TestContext.Current.CancellationToken));
    }
}

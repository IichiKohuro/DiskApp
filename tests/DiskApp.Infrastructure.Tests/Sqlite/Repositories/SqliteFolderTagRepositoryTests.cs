using static DiskApp.Infrastructure.Tests.Sqlite.Repositories.IndexTestContext;

namespace DiskApp.Infrastructure.Tests.Sqlite.Repositories;

/// <summary>
/// Авто-теги папок (PLAN.md, этап 19): замена набора, путь в другом регистре — та же папка, перенос путей со слиянием,
/// удаление тега убирает его и у папок.
/// </summary>
public sealed class SqliteFolderTagRepositoryTests
{
    [Fact]
    public async Task Set_ReplacesFolderSet_EmptyListClears()
    {
        // Arrange
        await using var context = await CreateAsync();
        var folder = Path.Combine(context.Root.Path, "417");
        var spec = await context.Tags.AddAsync("ТЗ", "#E74856", TestContext.Current.CancellationToken);
        var bridge = await context.Tags.AddAsync("Мост", null, TestContext.Current.CancellationToken);

        // Act
        await context.FolderTags.SetAsync(folder, [spec.Id, bridge.Id, spec.Id], TestContext.Current.CancellationToken);
        var first = await context.FolderTags.GetAllAsync(TestContext.Current.CancellationToken);
        await context.FolderTags.SetAsync(folder, [bridge.Id], TestContext.Current.CancellationToken);
        var second = await context.FolderTags.GetAllAsync(TestContext.Current.CancellationToken);
        await context.FolderTags.SetAsync(folder, [], TestContext.Current.CancellationToken);
        var cleared = await context.FolderTags.GetAllAsync(TestContext.Current.CancellationToken);

        // Assert
        first.ShouldHaveSingleItem().Path.ShouldBe(folder);
        first[0].Tags.Select(static tag => tag.Name).ShouldBe(["Мост", "ТЗ"]);
        first[0].Tags.Single(static tag => tag.Name == "ТЗ").Color.ShouldBe("#E74856");
        second.ShouldHaveSingleItem().Tags.ShouldHaveSingleItem().Name.ShouldBe("Мост");
        cleared.ShouldBeEmpty();
    }

    [Fact]
    public async Task Set_SameFolderInOtherCase_ReplacesOnWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Регистр путей не важен только в Windows.");

        // Arrange
        await using var context = await CreateAsync();
        var tag = await context.Tags.AddAsync("ТЗ", null, TestContext.Current.CancellationToken);
        await context.FolderTags.SetAsync(Path.Combine(context.Root.Path, "Проект"), [tag.Id], TestContext.Current.CancellationToken);

        // Act
        await context.FolderTags.SetAsync(Path.Combine(context.Root.Path, "ПРОЕКТ"), [tag.Id], TestContext.Current.CancellationToken);

        // Assert
        (await context.FolderTags.GetAllAsync(TestContext.Current.CancellationToken)).ShouldHaveSingleItem().Path.ShouldEndWith("ПРОЕКТ");
    }

    [Fact]
    public async Task MovePaths_MovesAndMergesWithExistingRows()
    {
        // Arrange: у нового пути уже есть один из тегов — строка остаётся одна
        await using var context = await CreateAsync();
        var from = Path.Combine(context.Root.Path, "417");
        var to = Path.Combine(context.Root.Path, "417 Мост");
        var spec = await context.Tags.AddAsync("ТЗ", null, TestContext.Current.CancellationToken);
        var bridge = await context.Tags.AddAsync("Мост", null, TestContext.Current.CancellationToken);
        await context.FolderTags.SetAsync(from, [spec.Id, bridge.Id], TestContext.Current.CancellationToken);
        await context.FolderTags.SetAsync(to, [bridge.Id], TestContext.Current.CancellationToken);

        // Act
        await context.FolderTags.MovePathsAsync([(from, to)], TestContext.Current.CancellationToken);

        // Assert
        var folder = (await context.FolderTags.GetAllAsync(TestContext.Current.CancellationToken)).ShouldHaveSingleItem();
        folder.Path.ShouldBe(to);
        folder.Tags.Select(static tag => tag.Name).ShouldBe(["Мост", "ТЗ"]);
    }

    [Fact]
    public async Task DeleteTag_RemovesItFromFolders()
    {
        // Arrange
        await using var context = await CreateAsync();
        var spec = await context.Tags.AddAsync("ТЗ", null, TestContext.Current.CancellationToken);
        var bridge = await context.Tags.AddAsync("Мост", null, TestContext.Current.CancellationToken);
        await context.FolderTags.SetAsync(Path.Combine(context.Root.Path, "417"), [spec.Id], TestContext.Current.CancellationToken);
        await context.FolderTags.SetAsync(Path.Combine(context.Root.Path, "418"), [spec.Id, bridge.Id], TestContext.Current.CancellationToken);

        // Act
        await context.Tags.DeleteAsync(spec.Id, TestContext.Current.CancellationToken);

        // Assert
        var folder = (await context.FolderTags.GetAllAsync(TestContext.Current.CancellationToken)).ShouldHaveSingleItem();
        folder.Path.ShouldEndWith("418");
        folder.Tags.ShouldHaveSingleItem().Name.ShouldBe("Мост");
    }
}

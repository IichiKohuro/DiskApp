using DiskApp.Core.Domain;

using Microsoft.Data.Sqlite;

using static DiskApp.Infrastructure.Tests.Sqlite.Repositories.IndexTestContext;

namespace DiskApp.Infrastructure.Tests.Sqlite.Repositories;

/// <summary>
/// Проекты в SQLite (PLAN.md, этап 24): номер уникален без учёта регистра, заметки привязываются к проекту и считаются
/// без корзины, удаление проекта оставляет заметки без проекта, папка проекта следует за переименованием.
/// </summary>
public sealed class SqliteProjectRepositoryTests
{
    [Fact]
    public async Task AddFindUpdate_NumberUniqueIgnoringCase()
    {
        // Arrange
        await using var context = await CreateAsync();
        var ct = TestContext.Current.CancellationToken;

        // Act
        var project = await context.Projects.AddAsync(new Project { Number = "10S-417", Title = "Мост", Color = "#0078D4", FolderPath = @"D:\Работа\417" }, Now, ct);
        var found = await context.Projects.FindByNumberAsync("10s-417 ", ct);
        var duplicate = await Should.ThrowAsync<SqliteException>(() => context.Projects.AddAsync(new Project { Number = "10S-417" }, Now, ct));
        var updated = await context.Projects.UpdateAsync(project with { Title = "Мост через Обь", FolderPath = null }, Now.AddHours(1), ct);
        var missing = await context.Projects.UpdateAsync(project with { Id = 999 }, Now, ct);

        // Assert
        found!.Id.ShouldBe(project.Id);
        duplicate.SqliteErrorCode.ShouldBe(19, "UNIQUE");
        (updated!.Title, updated.FolderPath, updated.CreatedUtc, updated.ModifiedUtc).ShouldBe(("Мост через Обь", null, Now, Now.AddHours(1)));
        missing.ShouldBeNull();
    }

    [Fact]
    public async Task Notes_LinkedAndCountedWithoutTrash_DeleteKeepsNotes()
    {
        // Arrange
        await using var context = await CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        var project = await context.Projects.AddAsync(new Project { Number = "417" }, Now, ct);
        var first = await context.Notes.AddAsync("Звонок", false, [], Now, project.Id, ct);
        var second = await context.Notes.AddAsync("Смета", false, [], Now, project.Id, ct);
        await context.Notes.AddAsync("Покупки", false, [], Now, cancellationToken: ct);
        await context.Notes.TrashAsync(second.Id, Now, ct);

        // Act
        var counted = (await context.Projects.GetAsync(project.Id, ct))!.NoteCount;
        var ofProject = await context.Notes.ListAsync(new NoteQuery(ProjectId: project.Id), ct);
        var deleted = await context.Projects.DeleteAsync(project.Id, ct);
        var note = await context.Notes.GetAsync(first.Id, ct);

        // Assert
        counted.ShouldBe(1);
        ofProject.ShouldHaveSingleItem().Id.ShouldBe(first.Id);
        deleted.ShouldBeTrue();
        note!.ProjectId.ShouldBeNull();
        (await context.Projects.GetAllAsync(ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task MoveFolder_ChangesExactPathOnly()
    {
        // Arrange
        await using var context = await CreateAsync();
        var ct = TestContext.Current.CancellationToken;
        await context.Projects.AddAsync(new Project { Number = "417", FolderPath = @"D:\Работа\417" }, Now, ct);
        await context.Projects.AddAsync(new Project { Number = "418", FolderPath = @"D:\Работа\418" }, Now, ct);

        // Act
        var moved = await context.Projects.MoveFolderAsync(@"D:\Работа\417", @"D:\Архив\417", ct);

        // Assert
        moved.ShouldBe(1);
        (await context.Projects.GetAllAsync(ct)).Select(static project => project.FolderPath).ShouldBe([@"D:\Архив\417", @"D:\Работа\418"]);
    }
}

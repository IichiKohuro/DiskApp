using DiskApp.Core.Domain;

using static DiskApp.Infrastructure.Tests.Sqlite.Repositories.IndexTestContext;

namespace DiskApp.Infrastructure.Tests.Sqlite.Repositories;

/// <summary>
/// Заметки в SQLite (PLAN.md, этап 23): заголовок из первой строки, поиск без учёта регистра и «ё», закреплённые сверху,
/// свои теги отдельно от тегов файлов, корзина с возвратом и очисткой через 30 дней.
/// </summary>
public sealed class SqliteNoteRepositoryTests
{
    [Fact]
    public async Task Add_TitleFromFirstLine_UpdateChangesTitleAndModified()
    {
        // Arrange
        await using var context = await CreateAsync();

        // Act
        var note = await context.Notes.AddAsync("\n# Звонок по проекту 417\n\nТекст", false, [], Now, TestContext.Current.CancellationToken);
        var updated = await context.Notes.UpdateAsync(note.Id, "- [ ] Купить бумагу", true, [], Now.AddMinutes(5), TestContext.Current.CancellationToken);

        // Assert
        note.Title.ShouldBe("Звонок по проекту 417");
        note.CreatedUtc.ShouldBe(Now);
        updated.ShouldNotBeNull();
        updated.Title.ShouldBe("Купить бумагу");
        updated.IsPinned.ShouldBeTrue();
        updated.CreatedUtc.ShouldBe(Now);
        updated.ModifiedUtc.ShouldBe(Now.AddMinutes(5));
        (await context.Notes.UpdateAsync(999, "нет такой", false, [], Now, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task List_SearchIgnoresCaseAndYo_AllTermsMustMatch()
    {
        // Arrange
        await using var context = await CreateAsync();
        var hedgehog = await context.Notes.AddAsync("Ёжик в тумане\nМультфильм Норштейна", false, [], Now, TestContext.Current.CancellationToken);
        await context.Notes.AddAsync("Туманный Альбион", false, [], Now.AddMinutes(1), TestContext.Current.CancellationToken);

        // Act
        var byYo = await context.Notes.ListAsync(new NoteQuery("ЕЖИК"), TestContext.Current.CancellationToken);
        var byBody = await context.Notes.ListAsync(new NoteQuery("норштейн туман"), TestContext.Current.CancellationToken);
        var both = await context.Notes.ListAsync(new NoteQuery("туман"), TestContext.Current.CancellationToken);
        var none = await context.Notes.ListAsync(new NoteQuery("ёжик альбион"), TestContext.Current.CancellationToken);

        // Assert
        byYo.ShouldHaveSingleItem().Id.ShouldBe(hedgehog.Id);
        byBody.ShouldHaveSingleItem().Id.ShouldBe(hedgehog.Id);
        both.Count.ShouldBe(2);
        none.ShouldBeEmpty();
    }

    [Fact]
    public async Task List_PinnedFirst_ThenNewestModified()
    {
        // Arrange
        await using var context = await CreateAsync();
        var old = await context.Notes.AddAsync("Старая", false, [], Now, TestContext.Current.CancellationToken);
        var pinned = await context.Notes.AddAsync("Закреплённая", true, [], Now.AddMinutes(1), TestContext.Current.CancellationToken);
        var fresh = await context.Notes.AddAsync("Свежая", false, [], Now.AddMinutes(2), TestContext.Current.CancellationToken);

        // Act
        var list = await context.Notes.ListAsync(new NoteQuery(), TestContext.Current.CancellationToken);

        // Assert
        list.Select(static note => note.Id).ShouldBe([pinned.Id, fresh.Id, old.Id]);
    }

    [Fact]
    public async Task NoteTags_SeparateFromFileTags_FilterAndCascadeOnDelete()
    {
        // Arrange
        await using var context = await CreateAsync();
        var fileTag = await context.Tags.AddAsync("Работа", "#ff0000", TestContext.Current.CancellationToken);
        var noteTag = await context.NoteTags.AddAsync("работа", null, TestContext.Current.CancellationToken);
        var other = await context.NoteTags.AddAsync("Идеи", "#00ff00", TestContext.Current.CancellationToken);
        var work = await context.Notes.AddAsync("План на неделю", false, [noteTag.Id, other.Id, noteTag.Id], Now, TestContext.Current.CancellationToken);
        await context.Notes.AddAsync("Без тегов", false, [], Now, TestContext.Current.CancellationToken);

        // Act
        var filtered = await context.Notes.ListAsync(new NoteQuery(TagId: noteTag.Id), TestContext.Current.CancellationToken);
        var sameName = await context.NoteTags.AddAsync("  РАБОТА ", "#123456", TestContext.Current.CancellationToken);
        var deleted = await context.NoteTags.DeleteAsync(noteTag.Id, TestContext.Current.CancellationToken);
        var afterDelete = await context.Notes.GetAsync(work.Id, TestContext.Current.CancellationToken);

        // Assert: одно имя у тега файлов и тега заметок — это разные теги
        noteTag.Id.ShouldNotBe(0);
        work.Tags.Select(static tag => tag.Name).ShouldBe(["Идеи", "работа"]);
        filtered.ShouldHaveSingleItem().Id.ShouldBe(work.Id);
        sameName.ShouldBe(noteTag);
        deleted.ShouldBeTrue();
        afterDelete.ShouldNotBeNull().Tags.ShouldHaveSingleItem().Name.ShouldBe("Идеи");
        (await context.Tags.GetAllAsync(TestContext.Current.CancellationToken)).ShouldHaveSingleItem().ShouldBe(fileTag);
        (await context.NoteTags.GetAllAsync(TestContext.Current.CancellationToken)).ShouldHaveSingleItem().Name.ShouldBe("Идеи");
    }

    [Fact]
    public async Task Trash_HidesFromList_RestoreReturns_PurgeOnlyFromTrash()
    {
        // Arrange
        await using var context = await CreateAsync();
        var note = await context.Notes.AddAsync("В корзину", false, [], Now, TestContext.Current.CancellationToken);
        var kept = await context.Notes.AddAsync("Остаётся", false, [], Now, TestContext.Current.CancellationToken);

        // Act
        var purgedLive = await context.Notes.PurgeAsync(kept.Id, TestContext.Current.CancellationToken);
        var trashed = await context.Notes.TrashAsync(note.Id, Now.AddHours(1), TestContext.Current.CancellationToken);
        var editedInTrash = await context.Notes.UpdateAsync(note.Id, "Правка в корзине", false, [], Now, TestContext.Current.CancellationToken);
        var live = await context.Notes.ListAsync(new NoteQuery(), TestContext.Current.CancellationToken);
        var trash = await context.Notes.ListAsync(new NoteQuery(Deleted: true), TestContext.Current.CancellationToken);
        var restored = await context.Notes.RestoreAsync(note.Id, TestContext.Current.CancellationToken);
        var afterRestore = await context.Notes.ListAsync(new NoteQuery(), TestContext.Current.CancellationToken);
        await context.Notes.TrashAsync(note.Id, Now.AddHours(2), TestContext.Current.CancellationToken);
        var purged = await context.Notes.PurgeAsync(note.Id, TestContext.Current.CancellationToken);

        // Assert
        purgedLive.ShouldBeFalse();
        trashed.ShouldBeTrue();
        editedInTrash.ShouldBeNull();
        live.ShouldHaveSingleItem().Id.ShouldBe(kept.Id);
        trash.ShouldHaveSingleItem().DeletedUtc.ShouldBe(Now.AddHours(1));
        restored.ShouldBeTrue();
        afterRestore.Count.ShouldBe(2);
        purged.ShouldBeTrue();
        (await context.Notes.GetAsync(note.Id, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task PurgeDeletedBefore_RemovesOnlyOldTrash()
    {
        // Arrange
        await using var context = await CreateAsync();
        var old = await context.Notes.AddAsync("Давно удалена", false, [], Now, TestContext.Current.CancellationToken);
        var recent = await context.Notes.AddAsync("Удалена вчера", false, [], Now, TestContext.Current.CancellationToken);
        var live = await context.Notes.AddAsync("Живая", false, [], Now.AddDays(-90), TestContext.Current.CancellationToken);
        await context.Notes.TrashAsync(old.Id, Now.AddDays(-31), TestContext.Current.CancellationToken);
        await context.Notes.TrashAsync(recent.Id, Now.AddDays(-1), TestContext.Current.CancellationToken);

        // Act
        var removed = await context.Notes.PurgeDeletedBeforeAsync(Now.AddDays(-30), TestContext.Current.CancellationToken);

        // Assert
        removed.ShouldBe(1);
        (await context.Notes.GetAsync(old.Id, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await context.Notes.GetAsync(recent.Id, TestContext.Current.CancellationToken)).ShouldNotBeNull();
        (await context.Notes.GetAsync(live.Id, TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }
}

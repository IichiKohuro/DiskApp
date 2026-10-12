using System.Net;

using DiskApp.Client;
using DiskApp.Contracts.Library;
using DiskApp.Contracts.Notes;
using DiskApp.Core.Abstractions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DiskApp.Server.Tests.Api;

/// <summary>
/// Заметки через API и клиент (PLAN.md, этап 23): создание с тегами по именам, правка, поиск и фильтр по тегу, корзина
/// с возвратом, удаление навсегда только из корзины, очистка корзины через 30 дней, ошибки 400/404/409.
/// </summary>
public sealed class NotesApiTests
{
    [Fact]
    public async Task Scenario_CreateEditSearchTrashRestore_WorksThroughClient()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        using var services = Client.ClientApiTests.Services(context);
        var api = services.GetRequiredService<IDiskAppApi>();

        // Act
        var created = await api.CreateNoteAsync(new NoteRequest { Body = "# Мост 417\n\n- [ ] Сдать чертежи", Tags = ["Проект", "проект", " срочно "] }, TestContext.Current.CancellationToken);
        await api.CreateNoteAsync(new NoteRequest { Body = "Список покупок" }, TestContext.Current.CancellationToken);
        var edited = await api.UpdateNoteAsync(created.Id, new NoteRequest { Body = "# Мост 417\n\n- [x] Сдать чертежи", IsPinned = true, Tags = ["Проект"] }, TestContext.Current.CancellationToken);
        var tags = await api.GetNoteTagsAsync(TestContext.Current.CancellationToken);
        var project = tags.Single(static tag => tag.Name == "Проект");
        var found = await api.GetNotesAsync("ЧЕРТЕЖИ", cancellationToken: TestContext.Current.CancellationToken);
        var byTag = await api.GetNotesAsync(tagId: project.Id, cancellationToken: TestContext.Current.CancellationToken);
        var fileTags = await api.GetTagsAsync(TestContext.Current.CancellationToken);
        await api.DeleteNoteAsync(created.Id, cancellationToken: TestContext.Current.CancellationToken);
        var afterTrash = await api.GetNotesAsync(cancellationToken: TestContext.Current.CancellationToken);
        var trash = await api.GetNotesAsync(deleted: true, cancellationToken: TestContext.Current.CancellationToken);
        var restored = await api.RestoreNoteAsync(created.Id, TestContext.Current.CancellationToken);
        var all = await api.GetNotesAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        created.Title.ShouldBe("Мост 417");
        created.Tags.Select(static tag => tag.Name).ShouldBe(["Проект", "срочно"], ignoreOrder: true);
        edited.IsPinned.ShouldBeTrue();
        edited.Body.ShouldContain("[x]");
        edited.Tags.ShouldHaveSingleItem().Name.ShouldBe("Проект");
        tags.Count.ShouldBe(2, "тег «срочно» снят с заметки, но остаётся в списке тегов заметок");
        found.ShouldHaveSingleItem().Id.ShouldBe(created.Id);
        byTag.ShouldHaveSingleItem().Id.ShouldBe(created.Id);
        fileTags.ShouldBeEmpty("теги заметок — отдельный список, не теги файлов");
        afterTrash.ShouldHaveSingleItem().Title.ShouldBe("Список покупок");
        trash.ShouldHaveSingleItem().DeletedUtc.ShouldNotBeNull();
        restored.DeletedUtc.ShouldBeNull();
        all.Select(static note => note.Title).ShouldBe(["Мост 417", "Список покупок"], "закреплённая — сверху");
    }

    [Fact]
    public async Task PermanentDelete_OnlyFromTrash()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        using var services = Client.ClientApiTests.Services(context);
        var api = services.GetRequiredService<IDiskAppApi>();
        var note = await api.CreateNoteAsync(new NoteRequest { Body = "Черновик" }, TestContext.Current.CancellationToken);

        // Act
        var live = await Should.ThrowAsync<DiskAppApiException>(() => api.DeleteNoteAsync(note.Id, permanent: true, TestContext.Current.CancellationToken));
        await api.DeleteNoteAsync(note.Id, cancellationToken: TestContext.Current.CancellationToken);
        await api.DeleteNoteAsync(note.Id, permanent: true, TestContext.Current.CancellationToken);
        var gone = await Should.ThrowAsync<DiskAppApiException>(() => api.GetNoteAsync(note.Id, TestContext.Current.CancellationToken));
        var restoreGone = await Should.ThrowAsync<DiskAppApiException>(() => api.RestoreNoteAsync(note.Id, TestContext.Current.CancellationToken));

        // Assert
        live.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        gone.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        restoreGone.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Trash_OlderThan30Days_PurgedOnNextList()
    {
        // Arrange
        var clock = new MovableClock(new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero));
        await using var context = await ServerTestContext.StartAsync(true, "Production", services => services.Replace(ServiceDescriptor.Singleton<IClock>(clock)));
        using var services = Client.ClientApiTests.Services(context);
        var api = services.GetRequiredService<IDiskAppApi>();
        var old = await api.CreateNoteAsync(new NoteRequest { Body = "Старая" }, TestContext.Current.CancellationToken);
        var recent = await api.CreateNoteAsync(new NoteRequest { Body = "Недавняя" }, TestContext.Current.CancellationToken);
        await api.DeleteNoteAsync(old.Id, cancellationToken: TestContext.Current.CancellationToken);
        clock.UtcNow += TimeSpan.FromDays(20);
        await api.DeleteNoteAsync(recent.Id, cancellationToken: TestContext.Current.CancellationToken);

        // Act
        clock.UtcNow += TimeSpan.FromDays(11);
        var trash = await api.GetNotesAsync(deleted: true, cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        trash.ShouldHaveSingleItem().Id.ShouldBe(recent.Id);
    }

    [Fact]
    public async Task Invalid_Missing_Duplicate_ReturnProblems()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        using var services = Client.ClientApiTests.Services(context);
        var api = services.GetRequiredService<IDiskAppApi>();
        var tag = await api.CreateNoteTagAsync(new TagRequest { Name = "Идеи", Color = "#3366FF" }, TestContext.Current.CancellationToken);
        var other = await api.CreateNoteTagAsync(new TagRequest { Name = "Дом" }, TestContext.Current.CancellationToken);

        // Act
        var tooLong = await Should.ThrowAsync<DiskAppApiException>(() => api.CreateNoteAsync(new NoteRequest { Body = new string('я', NoteRequest.MaxBodyLength + 1) }, TestContext.Current.CancellationToken));
        var tooMany = await Should.ThrowAsync<DiskAppApiException>(() => api.CreateNoteAsync(
            new NoteRequest { Body = "x", Tags = [.. Enumerable.Range(1, NoteRequest.MaxTags + 1).Select(static index => "t" + index)] }, TestContext.Current.CancellationToken));
        var longTag = await Should.ThrowAsync<DiskAppApiException>(() => api.CreateNoteAsync(new NoteRequest { Body = "x", Tags = [new string('я', 65)] }, TestContext.Current.CancellationToken));
        var missing = await Should.ThrowAsync<DiskAppApiException>(() => api.UpdateNoteAsync(987654, new NoteRequest { Body = "x" }, TestContext.Current.CancellationToken));
        var duplicate = await Should.ThrowAsync<DiskAppApiException>(() => api.CreateNoteTagAsync(new TagRequest { Name = " идеи " }, TestContext.Current.CancellationToken));
        var renameClash = await Should.ThrowAsync<DiskAppApiException>(() => api.UpdateNoteTagAsync(other.Id, new TagRequest { Name = "ИДЕИ" }, TestContext.Current.CancellationToken));
        var badColor = await Should.ThrowAsync<DiskAppApiException>(() => api.CreateNoteTagAsync(new TagRequest { Name = "Цвет", Color = "red" }, TestContext.Current.CancellationToken));
        var renamed = await api.UpdateNoteTagAsync(tag.Id, new TagRequest { Name = "идеи", Color = "#3366FF" }, TestContext.Current.CancellationToken);
        await api.DeleteNoteTagAsync(other.Id, TestContext.Current.CancellationToken);
        var deletedTwice = await Should.ThrowAsync<DiskAppApiException>(() => api.DeleteNoteTagAsync(other.Id, TestContext.Current.CancellationToken));

        // Assert
        tooLong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        tooLong.Errors.ShouldContainKey("Body");
        tooMany.Errors.ShouldContainKey("Tags");
        longTag.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        renameClash.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        badColor.Errors.ShouldContainKey("Color");
        renamed.Name.ShouldBe("идеи", "смена регистра своего тега — не конфликт");
        deletedTwice.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private sealed class MovableClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }
}

using System.Net;

using DiskApp.Client;
using DiskApp.Contracts.Notes;
using DiskApp.Contracts.Projects;

using Microsoft.Extensions.DependencyInjection;

namespace DiskApp.Server.Tests.Api;

/// <summary>
/// Проекты через API и клиент (PLAN.md, этап 24): номер свой и уникальный (409), порядок «2, 10, 417», неверный запрос —
/// 400 по полям, заметки проекта и фильтр по нему, удаление проекта оставляет заметки, папка проекта следует за
/// переименованием на диске.
/// </summary>
public sealed class ProjectsApiTests
{
    [Fact]
    public async Task Create_ListByNumber_DuplicateAndInvalidRejected()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        using var services = Client.ClientApiTests.Services(context);
        var api = services.GetRequiredService<IDiskAppApi>();
        var ct = TestContext.Current.CancellationToken;

        // Act
        foreach (var number in new[] { "417", "2", "10S-417", "10" })
        {
            await api.CreateProjectAsync(new ProjectRequest { Number = number, Title = "Проект " + number }, ct);
        }

        var list = await api.GetProjectsAsync(ct);
        var duplicate = await Should.ThrowAsync<DiskAppApiException>(() => api.CreateProjectAsync(new ProjectRequest { Number = " 10s-417 " }, ct));
        var invalid = await Should.ThrowAsync<DiskAppApiException>(() => api.CreateProjectAsync(new ProjectRequest { Number = "", Color = "red", FolderPath = @"Работа\417" }, ct));

        // Assert
        list.Select(static project => project.Number).ShouldBe(["2", "10", "10S-417", "417"]);
        (duplicate.StatusCode, duplicate.Code).ShouldBe((HttpStatusCode.Conflict, "project.duplicate"));
        invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        invalid.Errors.Keys.ShouldBe(["number", "color", "folderPath"], ignoreOrder: true);
    }

    [Fact]
    public async Task Notes_OfProject_FilteredCounted_DeleteKeepsThem()
    {
        // Arrange
        await using var context = await ServerTestContext.StartAsync();
        using var services = Client.ClientApiTests.Services(context);
        var api = services.GetRequiredService<IDiskAppApi>();
        var ct = TestContext.Current.CancellationToken;
        var project = await api.CreateProjectAsync(new ProjectRequest { Number = "417", Color = "#0078D4" }, ct);

        // Act
        var note = await api.CreateNoteAsync(new NoteRequest { Body = "Звонок по смете", ProjectId = project.Id }, ct);
        await api.CreateNoteAsync(new NoteRequest { Body = "Покупки" }, ct);
        var ofProject = await api.GetNotesAsync(projectId: project.Id, cancellationToken: ct);
        var counted = (await api.GetProjectsAsync(ct)).Single().NoteCount;
        var unknown = await Should.ThrowAsync<DiskAppApiException>(() => api.CreateNoteAsync(new NoteRequest { Body = "x", ProjectId = 999 }, ct));
        await api.DeleteProjectAsync(project.Id, ct);
        var after = await api.GetNoteAsync(note.Id, ct);
        var gone = await Should.ThrowAsync<DiskAppApiException>(() => api.UpdateProjectAsync(project.Id, new ProjectRequest { Number = "417" }, ct));

        // Assert
        note.ProjectId.ShouldBe(project.Id);
        ofProject.ShouldHaveSingleItem().Id.ShouldBe(note.Id);
        counted.ShouldBe(1);
        unknown.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        after.ProjectId.ShouldBeNull();
        gone.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task FolderRenamedOnDisk_ProjectFollows()
    {
        // Arrange: папка проекта внутри корня; переименование — как в Проводнике
        await using var context = await ServerTestContext.StartAsync();
        using var services = Client.ClientApiTests.Services(context);
        var api = services.GetRequiredService<IDiskAppApi>();
        var ct = TestContext.Current.CancellationToken;
        var folder = context.Files.CreateDirectory(Path.Combine("Работа", "417"));
        context.Files.CreateDirectory(Path.Combine("Работа", "417", "Акты"));
        await context.AddRootAsync();
        var project = await api.CreateProjectAsync(new ProjectRequest { Number = "417", FolderPath = folder }, ct);
        var renamed = context.Files.Resolve(Path.Combine("Работа", "417 Мост"));

        // Act
        Directory.Move(folder, renamed);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        string? path = null;
        while (DateTime.UtcNow < deadline && !string.Equals(path, renamed, StringComparison.OrdinalIgnoreCase))
        {
            await Task.Delay(100, ct);
            path = (await api.GetProjectsAsync(ct)).Single().FolderPath;
        }

        // Assert
        project.FolderPath.ShouldBe(folder);
        path.ShouldBe(renamed);
    }
}

using System.Text.RegularExpressions;

using DiskApp.Contracts;
using DiskApp.Contracts.Library;
using DiskApp.Contracts.Notes;
using DiskApp.Core.Abstractions;
using DiskApp.Core.Domain;
using DiskApp.Core.Repositories;
using DiskApp.Core.Search;
using DiskApp.Server.Localization;
using DiskApp.Server.Mapping;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace DiskApp.Server.Endpoints;

/// <summary>
/// Заметки (PLAN.md, этап 23, ADR-0014): список с поиском и фильтром по тегу, правка, закрепление, корзина заметок и свои
/// теги заметок. Заметки из корзины старше 30 дней удаляются навсегда при чтении списка.
/// </summary>
internal static partial class NotesEndpoints
{
    /// <summary>Сколько заметка лежит в корзине, прежде чем удалиться навсегда.</summary>
    public static readonly TimeSpan TrashRetention = TimeSpan.FromDays(30);

    public static void MapNotes(this IEndpointRouteBuilder endpoints)
    {
        var notes = endpoints.MapGroup(string.Empty).WithTags("Notes");

        notes.MapGet(ApiRoutes.V1.Notes, ListAsync)
            .WithName("GetNotes")
            .WithSummary("Заметки")
            .WithDescription("Закреплённые сверху, затем новые; q — слова в заголовке или тексте (без учёта регистра и «ё»), tagId — тег заметок, projectId — проект, deleted=true — корзина.");
        notes.MapPost(ApiRoutes.V1.Notes, CreateAsync)
            .WithName("CreateNote")
            .WithSummary("Новая заметка")
            .WithDescription("Теги — по именам; недостающие создаются в списке тегов заметок.");
        notes.MapGet(ApiRoutes.V1.NoteById, GetAsync).WithName("GetNote").WithSummary("Заметка");
        notes.MapPut(ApiRoutes.V1.NoteById, UpdateAsync).WithName("UpdateNote").WithSummary("Изменить заметку");
        notes.MapDelete(ApiRoutes.V1.NoteById, DeleteAsync)
            .WithName("DeleteNote")
            .WithSummary("Удалить заметку")
            .WithDescription("В корзину заметок; permanent=true — навсегда, только из корзины.");
        notes.MapPost(ApiRoutes.V1.NoteRestore, RestoreAsync).WithName("RestoreNote").WithSummary("Вернуть заметку из корзины");

        notes.MapGet(ApiRoutes.V1.NoteTags, GetTagsAsync).WithName("GetNoteTags").WithSummary("Теги заметок");
        notes.MapPost(ApiRoutes.V1.NoteTags, CreateTagAsync).WithName("CreateNoteTag").WithSummary("Создать тег заметок");
        notes.MapPut(ApiRoutes.V1.NoteTagById, UpdateTagAsync).WithName("UpdateNoteTag").WithSummary("Изменить тег заметок");
        notes.MapDelete(ApiRoutes.V1.NoteTagById, DeleteTagAsync).WithName("DeleteNoteTag").WithSummary("Удалить тег заметок");
    }

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex ColorPattern();

    private static async Task<Ok<NoteDto[]>> ListAsync(string? q, long? tagId, long? projectId, bool? deleted, int? limit, INoteRepository notes, IClock clock, CancellationToken cancellationToken)
    {
        await notes.PurgeDeletedBeforeAsync(clock.UtcNow - TrashRetention, cancellationToken).ConfigureAwait(false);
        var list = await notes.ListAsync(new NoteQuery(q, tagId, deleted ?? false, limit ?? 500, projectId), cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(list.Select(static note => note.ToDto()).ToArray());
    }

    private static async Task<Results<Ok<NoteDto>, ProblemHttpResult>> GetAsync(long id, INoteRepository notes, CancellationToken cancellationToken)
        => await notes.GetAsync(id, cancellationToken).ConfigureAwait(false) is { } note
            ? TypedResults.Ok(note.ToDto())
            : ApiProblems.NotFound(L.F("Notes.NotFound", id));

    private static async Task<Results<Created<NoteDto>, ValidationProblem>> CreateAsync(NoteRequest request, INoteRepository notes, INoteTagRepository tags, IProjectRepository projects, IClock clock, CancellationToken cancellationToken)
    {
        if ((Validate(request) ?? await ValidateProjectAsync(request, projects, cancellationToken).ConfigureAwait(false)) is { } invalid)
        {
            return invalid;
        }

        var tagIds = await TagIdsAsync(request, tags, cancellationToken).ConfigureAwait(false);
        var note = await notes.AddAsync(request.Body, request.IsPinned, tagIds, clock.UtcNow, request.ProjectId, cancellationToken).ConfigureAwait(false);
        return TypedResults.Created(ApiRoutes.V1.Note(note.Id), note.ToDto());
    }

    private static async Task<Results<Ok<NoteDto>, ValidationProblem, ProblemHttpResult>> UpdateAsync(long id, NoteRequest request, INoteRepository notes, INoteTagRepository tags, IProjectRepository projects, IClock clock, CancellationToken cancellationToken)
    {
        if ((Validate(request) ?? await ValidateProjectAsync(request, projects, cancellationToken).ConfigureAwait(false)) is { } invalid)
        {
            return invalid;
        }

        var tagIds = await TagIdsAsync(request, tags, cancellationToken).ConfigureAwait(false);
        return await notes.UpdateAsync(id, request.Body, request.IsPinned, tagIds, clock.UtcNow, request.ProjectId, cancellationToken).ConfigureAwait(false) is { } note
            ? TypedResults.Ok(note.ToDto())
            : ApiProblems.NotFound(L.F("Notes.NotFound", id));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(long id, bool? permanent, INoteRepository notes, IClock clock, CancellationToken cancellationToken)
    {
        var done = permanent == true
            ? await notes.PurgeAsync(id, cancellationToken).ConfigureAwait(false)
            : await notes.TrashAsync(id, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        return done
            ? TypedResults.NoContent()
            : ApiProblems.NotFound(permanent == true ? L.F("Notes.NotInTrash", id) : L.F("Notes.NotFound", id));
    }

    private static async Task<Results<Ok<NoteDto>, ProblemHttpResult>> RestoreAsync(long id, INoteRepository notes, CancellationToken cancellationToken)
    {
        if (!await notes.RestoreAsync(id, cancellationToken).ConfigureAwait(false))
        {
            return ApiProblems.NotFound(L.F("Notes.NotInTrash", id));
        }

        return TypedResults.Ok((await notes.GetAsync(id, cancellationToken).ConfigureAwait(false))!.ToDto());
    }

    private static async Task<Ok<TagDto[]>> GetTagsAsync(INoteTagRepository tags, CancellationToken cancellationToken)
        => TypedResults.Ok((await tags.GetAllAsync(cancellationToken).ConfigureAwait(false)).Select(static tag => tag.ToDto()).ToArray());

    private static async Task<Results<Created<TagDto>, ValidationProblem, ProblemHttpResult>> CreateTagAsync(TagRequest request, INoteTagRepository tags, CancellationToken cancellationToken)
    {
        if (ValidateTag(request) is { } invalid)
        {
            return invalid;
        }

        if (await FindByNameAsync(tags, request.Name, cancellationToken).ConfigureAwait(false) is not null)
        {
            return ApiProblems.Conflict("noteTag.duplicate", L.F("Library.TagExists", request.Name.Trim()));
        }

        var tag = await tags.AddAsync(request.Name.Trim(), request.Color, cancellationToken).ConfigureAwait(false);
        return TypedResults.Created(ApiRoutes.V1.NoteTag(tag.Id), tag.ToDto());
    }

    private static async Task<Results<Ok<TagDto>, ValidationProblem, ProblemHttpResult>> UpdateTagAsync(long id, TagRequest request, INoteTagRepository tags, CancellationToken cancellationToken)
    {
        if (ValidateTag(request) is { } invalid)
        {
            return invalid;
        }

        if (await FindByNameAsync(tags, request.Name, cancellationToken).ConfigureAwait(false) is { } other && other.Id != id)
        {
            return ApiProblems.Conflict("noteTag.duplicate", L.F("Library.TagExists", request.Name.Trim()));
        }

        var updated = new Tag(id, request.Name.Trim(), request.Color);
        return await tags.UpdateAsync(updated, cancellationToken).ConfigureAwait(false)
            ? TypedResults.Ok(updated.ToDto())
            : ApiProblems.NotFound(L.F("Library.TagNotFound", id));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteTagAsync(long id, INoteTagRepository tags, CancellationToken cancellationToken)
        => await tags.DeleteAsync(id, cancellationToken).ConfigureAwait(false)
            ? TypedResults.NoContent()
            : ApiProblems.NotFound(L.F("Library.TagNotFound", id));

    // Проект заметки (этап 24) должен существовать.
    private static async Task<ValidationProblem?> ValidateProjectAsync(NoteRequest request, IProjectRepository projects, CancellationToken cancellationToken)
        => request.ProjectId is { } projectId && await projects.GetAsync(projectId, cancellationToken).ConfigureAwait(false) is null
            ? ApiProblems.Invalid("projectId", L.F("Projects.NotFound", projectId))
            : null;

    private static async Task<Tag?> FindByNameAsync(INoteTagRepository tags, string name, CancellationToken cancellationToken)
    {
        var folded = SearchText.Fold(name.Trim());
        return (await tags.GetAllAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(tag => SearchText.Fold(tag.Name) == folded);
    }

    private static async Task<long[]> TagIdsAsync(NoteRequest request, INoteTagRepository tags, CancellationToken cancellationToken)
    {
        var ids = new List<long>();
        foreach (var name in Names(request))
        {
            ids.Add((await tags.AddAsync(name, cancellationToken: cancellationToken).ConfigureAwait(false)).Id);
        }

        return [.. ids.Distinct()];
    }

    private static string[] Names(NoteRequest request)
        => [.. (request.Tags ?? []).Select(static name => name?.Trim() ?? string.Empty).Where(static name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];

    private static ValidationProblem? Validate(NoteRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if ((request.Body ?? string.Empty).Length > NoteRequest.MaxBodyLength)
        {
            errors[nameof(request.Body)] = [L.F("Notes.TooLong", NoteRequest.MaxBodyLength)];
        }

        var names = Names(request);
        if (names.Length > NoteRequest.MaxTags)
        {
            errors[nameof(request.Tags)] = [L.F("Notes.TooManyTags", NoteRequest.MaxTags)];
        }
        else if (names.Any(static name => name.Length > 64))
        {
            errors[nameof(request.Tags)] = [L.T("LibraryEndpoints.ATagNameIs1To")];
        }

        request.Body ??= string.Empty;
        return errors.Count > 0 ? ApiProblems.Invalid(errors) : null;
    }

    private static ValidationProblem? ValidateTag(TagRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 64)
        {
            errors[nameof(request.Name)] = [L.T("LibraryEndpoints.ATagNameIs1To")];
        }

        if (request.Color is not null && !ColorPattern().IsMatch(request.Color))
        {
            errors[nameof(request.Color)] = [L.T("LibraryEndpoints.AColorLooksLikeRRGGBB")];
        }

        return errors.Count > 0 ? ApiProblems.Invalid(errors) : null;
    }
}

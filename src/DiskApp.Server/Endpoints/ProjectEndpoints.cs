using System.Text.RegularExpressions;

using DiskApp.Contracts;
using DiskApp.Contracts.Projects;
using DiskApp.Core.Abstractions;
using DiskApp.Core.Domain;
using DiskApp.Core.Paths;
using DiskApp.Core.Repositories;
using DiskApp.Server.Localization;
using DiskApp.Server.Mapping;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace DiskApp.Server.Endpoints;

/// <summary>
/// Проекты (PLAN.md, этап 24, ADR-0017): список по номеру («2» раньше «10»), создание, правка, удаление. Номер свой и
/// уникален без учёта регистра; основная рабочая папка — полный путь. Удаление проекта не трогает ни папку, ни заметки:
/// заметки остаются без проекта.
/// </summary>
internal static partial class ProjectEndpoints
{
    public static void MapProjects(this IEndpointRouteBuilder endpoints)
    {
        var projects = endpoints.MapGroup(string.Empty).WithTags("Projects");
        projects.MapGet(ApiRoutes.V1.Projects, ListAsync).WithName("GetProjects").WithSummary("Проекты по номеру");
        projects.MapPost(ApiRoutes.V1.Projects, CreateAsync)
            .WithName("CreateProject")
            .WithSummary("Новый проект")
            .WithDescription("Номер — 1–20 символов, свой у каждого проекта (иначе 409); папка — полный путь.");
        projects.MapGet(ApiRoutes.V1.ProjectById, GetAsync).WithName("GetProject").WithSummary("Проект");
        projects.MapPut(ApiRoutes.V1.ProjectById, UpdateAsync).WithName("UpdateProject").WithSummary("Изменить проект");
        projects.MapDelete(ApiRoutes.V1.ProjectById, DeleteAsync)
            .WithName("DeleteProject")
            .WithSummary("Удалить проект")
            .WithDescription("Папка и заметки остаются; заметки — без проекта.");
    }

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex ColorPattern();

    private static async Task<Ok<ProjectDto[]>> ListAsync(IProjectRepository projects, CancellationToken cancellationToken)
    {
        var list = await projects.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(list.OrderBy(static project => project.Number, Comparer<string>.Create(ProjectNumbers.Compare)).ThenBy(static project => project.Id).Select(static project => project.ToDto()).ToArray());
    }

    private static async Task<Results<Ok<ProjectDto>, ProblemHttpResult>> GetAsync(long id, IProjectRepository projects, CancellationToken cancellationToken)
        => await projects.GetAsync(id, cancellationToken).ConfigureAwait(false) is { } project
            ? TypedResults.Ok(project.ToDto())
            : ApiProblems.NotFound(L.F("Projects.NotFound", id));

    private static async Task<Results<Created<ProjectDto>, ValidationProblem, ProblemHttpResult>> CreateAsync(ProjectRequest request, IProjectRepository projects, PathNormalizer paths, IClock clock, CancellationToken cancellationToken)
    {
        if (Validate(request, paths, out var project) is { } invalid)
        {
            return invalid;
        }

        if (await projects.FindByNumberAsync(project.Number, cancellationToken).ConfigureAwait(false) is not null)
        {
            return ApiProblems.Conflict("project.duplicate", L.F("Projects.NumberTaken", project.Number));
        }

        var created = await projects.AddAsync(project, clock.UtcNow, cancellationToken).ConfigureAwait(false);
        return TypedResults.Created(ApiRoutes.V1.Project(created.Id), created.ToDto());
    }

    private static async Task<Results<Ok<ProjectDto>, ValidationProblem, ProblemHttpResult>> UpdateAsync(long id, ProjectRequest request, IProjectRepository projects, PathNormalizer paths, IClock clock, CancellationToken cancellationToken)
    {
        if (Validate(request, paths, out var project) is { } invalid)
        {
            return invalid;
        }

        if (await projects.FindByNumberAsync(project.Number, cancellationToken).ConfigureAwait(false) is { } other && other.Id != id)
        {
            return ApiProblems.Conflict("project.duplicate", L.F("Projects.NumberTaken", project.Number));
        }

        return await projects.UpdateAsync(project with { Id = id }, clock.UtcNow, cancellationToken).ConfigureAwait(false) is { } updated
            ? TypedResults.Ok(updated.ToDto())
            : ApiProblems.NotFound(L.F("Projects.NotFound", id));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(long id, IProjectRepository projects, CancellationToken cancellationToken)
        => await projects.DeleteAsync(id, cancellationToken).ConfigureAwait(false)
            ? TypedResults.NoContent()
            : ApiProblems.NotFound(L.F("Projects.NotFound", id));

    private static ValidationProblem? Validate(ProjectRequest request, PathNormalizer paths, out Project project)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var number = (request.Number ?? string.Empty).Trim();
        if (number.Length is 0 or > Project.MaxNumberLength || number.Any(char.IsControl))
        {
            errors["number"] = [L.F("Projects.NumberLength", Project.MaxNumberLength)];
        }

        var title = (request.Title ?? string.Empty).Trim();
        if (title.Length > Project.MaxTitleLength || title.Any(char.IsControl))
        {
            errors["title"] = [L.F("Projects.TitleLength", Project.MaxTitleLength)];
        }

        var color = string.IsNullOrWhiteSpace(request.Color) ? null : request.Color.Trim();
        if (color is not null && !ColorPattern().IsMatch(color))
        {
            errors["color"] = [L.T("Projects.ColorFormat")];
        }

        string? folder = null;
        if (!string.IsNullOrWhiteSpace(request.FolderPath))
        {
            if (Path.IsPathFullyQualified(request.FolderPath.Trim()) && paths.TryNormalize(request.FolderPath.Trim(), out var normalized))
            {
                folder = normalized;
            }
            else
            {
                errors["folderPath"] = [L.T("Projects.FolderMustBeFull")];
            }
        }

        project = new Project { Number = number, Title = title, Color = color, FolderPath = folder };
        return errors.Count > 0 ? ApiProblems.Invalid(errors) : null;
    }
}

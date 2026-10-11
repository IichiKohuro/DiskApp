using DiskApp.Contracts;
using DiskApp.Contracts.Roots;
using DiskApp.Core.Domain;
using DiskApp.Core.Indexing;
using DiskApp.Core.Paths;
using DiskApp.Core.Repositories;
using DiskApp.Server.Mapping;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace DiskApp.Server.Endpoints;

/// <summary>
/// Корни и индексация (PLAN.md, этап 4.3): список, добавление, изменение, удаление, пересканирование, пауза,
/// состояние и пересборка индекса. Удаление корня убирает его записи из индекса, файлы на диске не трогаются.
/// </summary>
internal static class RootEndpoints
{
    public const string PathMissingCode = "root.path_missing";

    public static void MapRoots(this IEndpointRouteBuilder endpoints)
    {
        var roots = endpoints.MapGroup(string.Empty).WithTags("Roots");

        roots.MapGet(ApiRoutes.V1.Roots, GetAllAsync)
            .WithName("GetRoots")
            .WithSummary("Список корней")
            .WithDescription("Все корни с текущим состоянием индексации: доступен, сканируется, на паузе, недоступен, ошибка.");

        roots.MapPost(ApiRoutes.V1.Roots, CreateAsync)
            .WithName("CreateRoot")
            .WithSummary("Добавить корень")
            .WithDescription("Путь должен быть абсолютным и существовать; тот же корень дважды не добавляется. После добавления начинается первое сканирование.");

        roots.MapPut(ApiRoutes.V1.RootById, UpdateAsync)
            .WithName("UpdateRoot")
            .WithSummary("Изменить корень")
            .WithDescription("Подпись, включение, скрытые и системные файлы, глубина. Путь не меняется. Изменение параметров обхода запускает сверку.");

        roots.MapDelete(ApiRoutes.V1.RootById, DeleteAsync)
            .WithName("DeleteRoot")
            .WithSummary("Удалить корень")
            .WithDescription("Останавливает индексацию корня и удаляет его записи, избранное и теги из индекса. Файлы на диске не трогаются.");

        roots.MapPost(ApiRoutes.V1.RootRescan, Rescan)
            .WithName("RescanRoot")
            .WithSummary("Пересканировать корень")
            .WithDescription("Ставит полное сканирование в очередь. 404 — корень не подключён к индексатору (выключен или удалён).");

        roots.MapPost(ApiRoutes.V1.RootPause, Pause)
            .WithName("PauseRoot")
            .WithSummary("Приостановить индексацию корня")
            .WithDescription("Задачи корня замирают, события наблюдателя копятся до возобновления. Пауза держится до перезапуска приложения.");

        roots.MapPost(ApiRoutes.V1.RootResume, Resume)
            .WithName("ResumeRoot")
            .WithSummary("Возобновить индексацию корня");

        var index = endpoints.MapGroup(string.Empty).WithTags("Index");

        index.MapGet(ApiRoutes.V1.IndexStatus, GetStatus)
            .WithName("GetIndexStatus")
            .WithSummary("Состояние индексации")
            .WithDescription("Общее состояние для иконки трея и состояние каждого корня с ходом выполняемой задачи.");

        index.MapPost(ApiRoutes.V1.IndexRebuild, RebuildAsync)
            .WithName("RebuildIndex")
            .WithSummary("Пересобрать индекс")
            .WithDescription("Удаляет записи всех корней и ставит полное сканирование каждого. Избранное и теги удалённых записей теряются.");
    }

    private static async Task<Ok<RootDto[]>> GetAllAsync(IRootRepository repository, IndexerService indexer, CancellationToken cancellationToken)
    {
        var statuses = indexer.GetStatuses().ToDictionary(static status => status.RootId);
        var roots = await repository.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(roots.Select(root => WithLiveStatus(root, statuses)).ToArray());
    }

    private static async Task<Results<Created<RootDto>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateRootRequest request,
        IRootRepository repository,
        IndexerService indexer,
        PathNormalizer paths,
        CancellationToken cancellationToken)
    {
        if (request.MaxDepth is < 0)
        {
            return ApiProblems.Invalid(nameof(request.MaxDepth), "Глубина не может быть отрицательной.");
        }

        var existing = await repository.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var validation = RootValidator.Validate(request.Path, existing, paths);
        if (!validation.TryGetValue(out var candidate))
        {
            return validation.Error!.Code == RootValidator.DuplicateCode
                ? ApiProblems.Domain(validation.Error, StatusCodes.Status409Conflict)
                : ApiProblems.Invalid(nameof(request.Path), validation.Error.Message);
        }

        if (!Directory.Exists(candidate.Path))
        {
            return ApiProblems.Invalid(nameof(request.Path), $"Папка «{candidate.Path}» не найдена или недоступна.");
        }

        var label = string.IsNullOrWhiteSpace(request.Label) ? candidate.Label : request.Label.Trim();
        var root = await repository.AddAsync(
            new Root
            {
                Path = candidate.Path,
                Label = label,
                IncludeHidden = request.IncludeHidden,
                IncludeSystem = request.IncludeSystem,
                MaxDepth = request.MaxDepth,
            },
            cancellationToken).ConfigureAwait(false);

        await indexer.RefreshRootsAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Created(ApiRoutes.V1.Root(root.Id), root.ToDto());
    }

    private static async Task<Results<Ok<RootDto>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        long id,
        UpdateRootRequest request,
        IRootRepository repository,
        IndexerService indexer,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Label))
        {
            return ApiProblems.Invalid(nameof(request.Label), "Подпись не может быть пустой.");
        }

        if (request.MaxDepth is < 0)
        {
            return ApiProblems.Invalid(nameof(request.MaxDepth), "Глубина не может быть отрицательной.");
        }

        var root = await repository.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return ApiProblems.NotFound($"Корень {id} не найден.");
        }

        var updated = root with
        {
            Label = request.Label.Trim(),
            IsEnabled = request.IsEnabled,
            IncludeHidden = request.IncludeHidden,
            IncludeSystem = request.IncludeSystem,
            MaxDepth = request.MaxDepth,
        };
        await repository.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
        await indexer.RefreshRootsAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(WithLiveStatus(updated, indexer.GetStatuses().ToDictionary(static status => status.RootId)));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        long id,
        IRootRepository repository,
        IndexerService indexer,
        CancellationToken cancellationToken)
    {
        var root = await repository.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return ApiProblems.NotFound($"Корень {id} не найден.");
        }

        // Сначала корень отключается от индексатора (задача отменяется и дожидается), потом удаляется с записями.
        if (root.IsEnabled)
        {
            await repository.UpdateAsync(root with { IsEnabled = false }, cancellationToken).ConfigureAwait(false);
            await indexer.RefreshRootsAsync(cancellationToken).ConfigureAwait(false);
        }

        await repository.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    private static Results<Accepted, ProblemHttpResult> Rescan(long id, IndexerService indexer)
    {
        if (!indexer.GetStatuses().Any(status => status.RootId == id))
        {
            return ApiProblems.NotFound($"Корень {id} не подключён к индексации.");
        }

        // false — такое же сканирование уже стоит в очереди: результат для клиента тот же.
        indexer.RescanRoot(id);
        return TypedResults.Accepted(ApiRoutes.V1.IndexStatus);
    }

    private static Results<NoContent, ProblemHttpResult> Pause(long id, IndexerService indexer)
        => indexer.PauseRoot(id) ? TypedResults.NoContent() : ApiProblems.NotFound($"Корень {id} не подключён к индексации.");

    private static Results<NoContent, ProblemHttpResult> Resume(long id, IndexerService indexer)
        => indexer.ResumeRoot(id) ? TypedResults.NoContent() : ApiProblems.NotFound($"Корень {id} не подключён к индексации.");

    private static Ok<IndexStatusDto> GetStatus(IndexerService indexer) => TypedResults.Ok(indexer.ToIndexStatusDto());

    private static async Task<Accepted> RebuildAsync(IndexerService indexer, CancellationToken cancellationToken)
    {
        await indexer.RebuildAllAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Accepted(ApiRoutes.V1.IndexStatus);
    }

    private static RootDto WithLiveStatus(Root root, Dictionary<long, RootIndexStatus> statuses)
        => statuses.TryGetValue(root.Id, out var status)
            ? root.ToDto() with { Status = status.Status.ToDto(), ErrorMessage = status.Message ?? root.ErrorMessage }
            : root.ToDto();
}

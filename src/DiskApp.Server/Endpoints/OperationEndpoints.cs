using DiskApp.Contracts;
using DiskApp.Contracts.Operations;
using DiskApp.Core.Abstractions;
using DiskApp.Core.Indexing;
using DiskApp.Core.Operations;
using DiskApp.Server.Mapping;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using ConflictPolicy = DiskApp.Contracts.Operations.ConflictPolicy;

namespace DiskApp.Server.Endpoints;

/// <summary>
/// Файловые операции и загрузка (PLAN.md, этапы 4.3 и 4.5): постановка, список, отмена, решение по конфликту,
/// отмена результата. Все пути — только внутри добавленных корней (403 иначе).
/// </summary>
internal static class OperationEndpoints
{
    /// <summary>Наибольший размер одной загрузки.</summary>
    public const long MaxUploadBytes = 2L * 1024 * 1024 * 1024;

    public static void MapOperations(this IEndpointRouteBuilder endpoints)
    {
        var operations = endpoints.MapGroup(string.Empty).WithTags("Operations");

        operations.MapPost(ApiRoutes.V1.Operations, EnqueueAsync)
            .WithName("EnqueueOperation")
            .WithSummary("Поставить файловую операцию")
            .WithDescription("Copy, Move, Rename, Delete, CreateFolder, CreateFile. Конфликты имён: Ask (операция ждёт решения), Skip, Overwrite "
                + "(заменяемый файл уходит в корзину, папки сливаются), KeepBoth. Delete — в корзину; безвозвратно только с Permanent = true. "
                + "Ход и завершение приходят событиями хаба.");

        operations.MapGet(ApiRoutes.V1.Operations, GetAll)
            .WithName("GetOperations")
            .WithSummary("Операции")
            .WithDescription("Незавершённые и последние завершённые операции, новые первыми.");

        operations.MapGet(ApiRoutes.V1.OperationById, Get)
            .WithName("GetOperation")
            .WithSummary("Операция");

        operations.MapPost(ApiRoutes.V1.OperationCancel, Cancel)
            .WithName("CancelOperation")
            .WithSummary("Отменить операцию")
            .WithDescription("Недокопированный файл удаляется; уже обработанное остаётся. 409 — операция уже завершена.");

        operations.MapPost(ApiRoutes.V1.OperationDecision, Decide)
            .WithName("DecideOperationConflict")
            .WithSummary("Решение по конфликту")
            .WithDescription("Skip, Overwrite, KeepBoth или Cancel; ApplyToAll — для всех следующих конфликтов операции. 409 — операция не ждёт решения.");

        operations.MapPost(ApiRoutes.V1.OperationUndo, Undo)
            .WithName("UndoOperation")
            .WithSummary("Отменить результат операции")
            .WithDescription("Для переименования, перемещения и создания в текущем сеансе: ставит обратную операцию. 409 — отменить нельзя.");

        operations.MapPost(ApiRoutes.V1.Upload, UploadAsync)
            .WithName("Upload")
            .WithSummary("Загрузить файлы в папку")
            .WithDescription("multipart/form-data; destination — папка внутри корней; conflicts — KeepBoth (по умолчанию), Skip или Overwrite. До 2 ГБ за запрос.")
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes), new RequestFormLimitsAttribute { MultipartBodyLengthLimit = MaxUploadBytes });
    }

    private static async Task<Results<Accepted<OperationDto>, ValidationProblem, ProblemHttpResult>> EnqueueAsync(
        OperationRequest request,
        FileOperationService service,
        RootGuard guard,
        CancellationToken cancellationToken)
    {
        var paths = request.Sources.Append(request.Destination).OfType<string>().ToArray();
        foreach (var path in paths)
        {
            var (allowed, normalized, _) = await guard.CheckAsync(path, cancellationToken).ConfigureAwait(false);
            if (!allowed)
            {
                return normalized is null
                    ? ApiProblems.Invalid("sources", $"Путь «{path}» должен быть абсолютным.")
                    : TypedResults.Problem($"Путь «{normalized}» вне добавленных корней.", statusCode: StatusCodes.Status403Forbidden, title: "Доступ запрещён");
            }
        }

        var result = service.Enqueue(request.ToDomain());
        return result.TryGetValue(out var info)
            ? TypedResults.Accepted(ApiRoutes.V1.Operation(info.Id), info.ToDto())
            : ApiProblems.Domain(result.Error!);
    }

    private static Ok<OperationDto[]> GetAll(FileOperationService service) => TypedResults.Ok(service.GetAll().Select(static info => info.ToDto()).ToArray());

    private static Results<Ok<OperationDto>, ProblemHttpResult> Get(Guid id, FileOperationService service)
        => service.Get(id) is { } info ? TypedResults.Ok(info.ToDto()) : ApiProblems.NotFound($"Операция {id} не найдена.");

    private static Results<NoContent, ProblemHttpResult> Cancel(Guid id, FileOperationService service)
    {
        var result = service.Cancel(id);
        return result.IsSuccess ? TypedResults.NoContent() : Failure(result.Error!);
    }

    private static Results<NoContent, ProblemHttpResult> Decide(Guid id, ConflictDecisionDto decision, FileOperationService service)
    {
        var result = service.Decide(id, (Core.Operations.ConflictResolution)(int)decision.Resolution, decision.ApplyToAll);
        return result.IsSuccess ? TypedResults.NoContent() : Failure(result.Error!);
    }

    private static Results<Accepted<OperationDto>, ProblemHttpResult> Undo(Guid id, FileOperationService service)
    {
        var result = service.Undo(id);
        return result.TryGetValue(out var info) ? TypedResults.Accepted(ApiRoutes.V1.Operation(info.Id), info.ToDto()) : Failure(result.Error!);
    }

    private static ProblemHttpResult Failure(Core.Results.DomainError error)
        => error.Code == FileOperationService.NotFoundCode
            ? ApiProblems.NotFound(error.Message)
            : ApiProblems.Domain(error, StatusCodes.Status409Conflict);

    private static async Task<Results<Ok<UploadResponse>, ValidationProblem, ProblemHttpResult>> UploadAsync(
        HttpRequest request,
        string? destination,
        ConflictPolicy? conflicts,
        RootGuard guard,
        IServiceProvider services,
        IPathChangeSink indexer,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            return ApiProblems.Invalid("body", "Нужен multipart/form-data.");
        }

        var (allowed, folder, _) = await guard.CheckAsync(destination, cancellationToken).ConfigureAwait(false);
        if (!allowed || folder is null)
        {
            return folder is null
                ? ApiProblems.Invalid("destination", "Укажите абсолютный путь папки.")
                : TypedResults.Problem($"Папка «{folder}» вне добавленных корней.", statusCode: StatusCodes.Status403Forbidden, title: "Доступ запрещён");
        }

        if (!Directory.Exists(folder))
        {
            return ApiProblems.NotFound($"Папка «{folder}» не найдена.");
        }

        var policy = conflicts ?? ConflictPolicy.KeepBoth;
        if (policy == ConflictPolicy.Ask)
        {
            return ApiProblems.Invalid("conflicts", "Для загрузки выберите KeepBoth, Skip или Overwrite.");
        }

        var form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var saved = new List<string>();
        var skipped = new List<string>();
        foreach (var file in form.Files)
        {
            var name = Path.GetFileName(file.FileName);
            if (FileOperationService.NameProblem(name) is { } problem)
            {
                return ApiProblems.Invalid("files", $"«{file.FileName}»: {problem}");
            }

            var target = Path.Combine(folder, name);
            if (File.Exists(target) || Directory.Exists(target))
            {
                switch (policy)
                {
                    case ConflictPolicy.Skip:
                        skipped.Add(name);
                        continue;
                    case ConflictPolicy.Overwrite:
                        if (services.GetService<IPlatformShell>() is not { } shell)
                        {
                            return ApiProblems.Unavailable("Замена файлов через корзину");
                        }

                        var recycled = await shell.MoveToRecycleBinAsync([target], cancellationToken).ConfigureAwait(false);
                        if (recycled.IsFailure)
                        {
                            return ApiProblems.Domain(recycled.Error!, StatusCodes.Status422UnprocessableEntity);
                        }

                        break;
                    default:
                        target = UniqueName(folder, name);
                        break;
                }
            }

            var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                await file.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
            }

            saved.Add(target);
        }

        if (saved.Count > 0)
        {
            indexer.OnPathsChanged([folder]);
        }

        return TypedResults.Ok(new UploadResponse(saved, skipped));
    }

    private static string UniqueName(string folder, string name)
    {
        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var extension = dot > 0 ? name[dot..] : string.Empty;
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({i.ToString(System.Globalization.CultureInfo.InvariantCulture)}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}

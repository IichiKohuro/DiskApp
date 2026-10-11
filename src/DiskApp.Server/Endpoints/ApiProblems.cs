using DiskApp.Core.Results;
using DiskApp.Server.Localization;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DiskApp.Server.Endpoints;

/// <summary>
/// Ответы с ошибками в формате ProblemDetails: единый вид для всех endpoints. Код доменной ошибки уходит
/// в расширение <c>code</c>, чтобы клиент мог отличать причины, не разбирая текст.
/// </summary>
internal static class ApiProblems
{
    public static ProblemHttpResult NotFound(string detail)
        => TypedResults.Problem(detail, statusCode: StatusCodes.Status404NotFound, title: L.T("ApiProblems.NotFound"));

    public static ValidationProblem Invalid(string field, string message)
        => TypedResults.ValidationProblem(new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] }, title: L.T("ApiProblems.BadRequest"));

    public static ValidationProblem Invalid(IReadOnlyDictionary<string, string[]> errors)
        => TypedResults.ValidationProblem(errors, title: L.T("ApiProblems.BadRequest"));

    public static ProblemHttpResult Domain(DomainError error, int statusCode = StatusCodes.Status400BadRequest)
        => TypedResults.Problem(
            error.Message,
            statusCode: statusCode,
            title: statusCode == StatusCodes.Status409Conflict ? L.T("ApiProblems.Conflict") : L.T("ApiProblems.TheOperationFailed"),
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = error.Code });

    public static ProblemHttpResult Conflict(string code, string detail) => Domain(new DomainError(code, detail), StatusCodes.Status409Conflict);

    public static ProblemHttpResult Unavailable(string feature)
        => TypedResults.Problem(
            L.F("ApiProblems.IsNotAvailableOnThisPlatform", feature),
            statusCode: StatusCodes.Status501NotImplemented,
            title: L.T("ApiProblems.NotSupported"));
}

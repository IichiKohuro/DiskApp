using System.Net;
using System.Text.Json.Serialization;

namespace DiskApp.Client;

/// <summary>
/// Ошибка API: код ответа и содержимое <c>ProblemDetails</c> — заголовок, пояснение, код доменной ошибки
/// (<c>root.duplicate</c>, <c>operation.invalid</c> и т. п.) и ошибки полей для неверных запросов.
/// </summary>
public sealed class DiskAppApiException : Exception
{
    /// <summary>
    /// Создаёт исключение.
    /// </summary>
    public DiskAppApiException(HttpStatusCode statusCode, string? title, string? detail, string? code, IReadOnlyDictionary<string, string[]>? errors)
        : base(detail ?? title ?? $"Сервер вернул {(int)statusCode}.")
    {
        StatusCode = statusCode;
        Title = title;
        Detail = detail;
        Code = code;
        Errors = errors ?? new Dictionary<string, string[]>();
    }

    /// <summary>Создаёт исключение без ответа сервера.</summary>
    public DiskAppApiException()
        : this(HttpStatusCode.InternalServerError, null, null, null, null)
    {
    }

    /// <summary>Создаёт исключение с сообщением.</summary>
    public DiskAppApiException(string message)
        : this(HttpStatusCode.InternalServerError, null, message, null, null)
    {
    }

    /// <summary>Создаёт исключение с сообщением и причиной.</summary>
    public DiskAppApiException(string message, Exception innerException)
        : base(message, innerException)
    {
        StatusCode = HttpStatusCode.InternalServerError;
        Detail = message;
        Errors = new Dictionary<string, string[]>();
    }

    /// <summary>Код ответа.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>Заголовок ошибки.</summary>
    public string? Title { get; }

    /// <summary>Пояснение для пользователя.</summary>
    public string? Detail { get; }

    /// <summary>Код доменной ошибки, если сервер его передал.</summary>
    public string? Code { get; }

    /// <summary>Ошибки полей запроса.</summary>
    public IReadOnlyDictionary<string, string[]> Errors { get; }
}

/// <summary>
/// Ответ с ошибкой в формате RFC 9457 (ProblemDetails) — то, что нужно клиенту.
/// </summary>
internal sealed record ApiProblem
{
    public string? Type { get; set; }

    public string? Title { get; set; }

    public int? Status { get; set; }

    public string? Detail { get; set; }

    public string? Code { get; set; }

    public Dictionary<string, string[]>? Errors { get; set; }
}

/// <summary>
/// Контекст сериализации типов, которые есть только у клиента.
/// </summary>
[JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ApiProblem))]
internal sealed partial class ClientJsonContext : JsonSerializerContext
{
}

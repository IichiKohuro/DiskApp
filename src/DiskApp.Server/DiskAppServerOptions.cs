using DiskApp.Core.Abstractions;

namespace DiskApp.Server;

/// <summary>
/// Параметры сервера внутри процесса (PLAN.md, этап 4.1).
/// </summary>
public sealed record DiskAppServerOptions
{
    /// <summary>Адрес по умолчанию: только loopback, порт выбирает система.</summary>
    public const string LoopbackDynamicUrl = "http://127.0.0.1:0";

    /// <summary>Папки данных: база, настройки, кэши.</summary>
    public required IAppPaths Paths { get; init; }

    /// <summary>
    /// Адрес Kestrel. Фиксированный порт и сеть — только при включённом удалённом доступе (этап 13).
    /// </summary>
    public string Url { get; init; } = LoopbackDynamicUrl;

    /// <summary>Окружение ASP.NET Core: <c>Development</c> включает OpenAPI и подробные ошибки.</summary>
    public string EnvironmentName { get; init; } = "Production";

    /// <summary>Запускать индексатор вместе с сервером. Тесты API выключают его, чтобы не трогать диск.</summary>
    public bool StartIndexer { get; init; } = true;

    /// <summary>
    /// Токен сессии (флаг): если задан, каждый запрос, кроме <c>/health</c>, должен нести его в заголовке
    /// <c>X-DiskApp-Token</c> — так другие локальные процессы не управляют файлами через API.
    /// </summary>
    public string? SessionToken { get; init; }

    /// <summary>Наибольший размер тела обычного запроса; загрузка файлов задаёт свой предел.</summary>
    public long MaxRequestBodyBytes { get; init; } = 10 * 1024 * 1024;

    /// <summary>Случайный токен сессии: 32 байта в Base64Url.</summary>
    public static string CreateSessionToken() => System.Buffers.Text.Base64Url.EncodeToString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

    // Токен не попадает в журнал, даже если параметры выводят целиком (этап 10.4).
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Url = ").Append(Url)
            .Append(", DataDirectory = ").Append(Paths?.DataDirectory)
            .Append(", EnvironmentName = ").Append(EnvironmentName)
            .Append(", StartIndexer = ").Append(StartIndexer)
            .Append(", SessionToken = ").Append(SessionToken is null ? "null" : "***")
            .Append(", MaxRequestBodyBytes = ").Append(MaxRequestBodyBytes);
        return true;
    }
}

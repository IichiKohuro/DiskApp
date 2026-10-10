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
}

namespace DiskApp.Contracts;

/// <summary>
/// Маршруты API — общие для сервера и клиентов, чтобы адреса не расходились (PLAN.md, этап 4.2).
/// </summary>
public static class ApiRoutes
{
    /// <summary>Проверка живости сервера; вне версии API.</summary>
    public const string Health = "/health";

    /// <summary>Маршруты первой версии API.</summary>
    public static class V1
    {
        /// <summary>Префикс всех маршрутов версии.</summary>
        public const string Prefix = "/api/v1";
    }
}

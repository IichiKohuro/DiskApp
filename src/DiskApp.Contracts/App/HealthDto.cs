using DiskApp.Contracts.Roots;

namespace DiskApp.Contracts.App;

/// <summary>
/// Ответ <c>GET /health</c>: сервер жив, база открыта, в каком состоянии индексация.
/// </summary>
/// <param name="Status">Всегда <c>ok</c>, если сервер ответил.</param>
/// <param name="Version">Версия приложения.</param>
/// <param name="UptimeSeconds">Сколько секунд работает сервер.</param>
/// <param name="SchemaVersion">Версия схемы базы.</param>
/// <param name="Indexer">Общее состояние индексации.</param>
public sealed record HealthDto(string Status, string Version, long UptimeSeconds, int SchemaVersion, IndexerState Indexer);

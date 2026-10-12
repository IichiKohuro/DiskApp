namespace DiskApp.Infrastructure.Tests.Sqlite;

/// <summary>
/// Тесты общей блокировки пула идут отдельно от остальных: удерживая её, они на время останавливали бы открытие
/// соединений во всех параллельных тестах, и тесты с бюджетом времени (отражение изменения за 1 с) падали бы.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqlitePoolGate
{
    /// <summary>Имя коллекции.</summary>
    public const string Name = "SqlitePoolGate";
}

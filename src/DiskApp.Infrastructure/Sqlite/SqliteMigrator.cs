using System.Globalization;
using System.Text.RegularExpressions;

using Microsoft.Data.Sqlite;

namespace DiskApp.Infrastructure.Sqlite;

/// <summary>
/// Миграции схемы: SQL-скрипты <c>V001__name.sql</c> из встроенных ресурсов применяются по порядку,
/// каждый в своей транзакции вместе с записью в <c>schema_version</c>. Повторный запуск ничего не делает.
/// </summary>
public static partial class SqliteMigrator
{
    private const string ResourcePrefix = "DiskApp.Infrastructure.Sqlite.Migrations.";

    /// <summary>Все известные миграции по возрастанию версии.</summary>
    public static IReadOnlyList<Migration> Migrations { get; } = LoadMigrations();

    /// <summary>Версия схемы, которую создаёт текущая сборка.</summary>
    public static int LatestVersion => Migrations[^1].Version;

    /// <summary>
    /// Текущая версия схемы базы; 0 для пустой базы без таблицы <c>schema_version</c>.
    /// </summary>
    public static int GetVersion(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'schema_version'";
        if (exists.ExecuteScalar() is null)
        {
            return 0;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Применяет недостающие миграции до <paramref name="targetVersion"/> (по умолчанию до последней) и возвращает применённые.
    /// </summary>
    public static IReadOnlyList<Migration> Apply(SqliteConnection connection, int? targetVersion = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var target = targetVersion ?? LatestVersion;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(target, LatestVersion);

        EnsureVersionTable(connection);
        var current = GetVersion(connection);
        var applied = new List<Migration>();

        foreach (var migration in Migrations)
        {
            if (migration.Version <= current || migration.Version > target)
            {
                continue;
            }

            using var transaction = connection.BeginTransaction();
            using (var script = connection.CreateCommand())
            {
                script.Transaction = transaction;
                script.CommandText = migration.Sql;
                script.ExecuteNonQuery();
            }

            using (var record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText = "INSERT INTO schema_version (version, name, applied_utc) VALUES ($version, $name, $applied)";
                record.Parameters.AddWithValue("$version", migration.Version);
                record.Parameters.AddWithValue("$name", migration.Name);
                record.Parameters.AddWithValue("$applied", SqliteTime.Encode(DateTimeOffset.UtcNow));
                record.ExecuteNonQuery();
            }

            transaction.Commit();
            applied.Add(migration);
        }

        return applied;
    }

    private static void EnsureVersionTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_version (
                version     INTEGER PRIMARY KEY,
                name        TEXT NOT NULL,
                applied_utc INTEGER NOT NULL
            )
            """;
        command.ExecuteNonQuery();
    }

    private static Migration[] LoadMigrations()
    {
        var assembly = typeof(SqliteMigrator).Assembly;
        var migrations = new List<Migration>();

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var match = MigrationNamePattern().Match(resourceName[ResourcePrefix.Length..]);
            if (!match.Success)
            {
                throw new InvalidOperationException($"Имя миграции «{resourceName}» не соответствует шаблону V001__name.sql.");
            }

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Ресурс «{resourceName}» не найден.");
            using var reader = new StreamReader(stream);

            migrations.Add(new Migration(
                int.Parse(match.Groups["version"].Value, CultureInfo.InvariantCulture),
                match.Groups["name"].Value,
                reader.ReadToEnd()));
        }

        if (migrations.Count == 0)
        {
            throw new InvalidOperationException("В сборке нет ни одной миграции схемы.");
        }

        migrations.Sort(static (left, right) => left.Version.CompareTo(right.Version));
        for (var i = 0; i < migrations.Count; i++)
        {
            if (migrations[i].Version != i + 1)
            {
                throw new InvalidOperationException($"Версии миграций должны идти подряд с 1; найдена V{migrations[i].Version:000} на позиции {i + 1}.");
            }
        }

        return [.. migrations];
    }

    [GeneratedRegex(@"^V(?<version>\d{3})__(?<name>[A-Za-z0-9_\-]+)\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex MigrationNamePattern();
}

/// <summary>
/// Одна миграция схемы.
/// </summary>
/// <param name="Version">Номер по порядку, с 1.</param>
/// <param name="Name">Имя из файла (<c>init</c> для <c>V001__init.sql</c>).</param>
/// <param name="Sql">Текст скрипта.</param>
public sealed record Migration(int Version, string Name, string Sql);

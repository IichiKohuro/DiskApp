using System.Globalization;

using Dapper;

using DiskApp.Core.Search;

using Microsoft.Data.Sqlite;

namespace DiskApp.Infrastructure.Sqlite;

/// <summary>
/// Пересчёт колонки <c>name_folded</c> после смены правил свёртки (<see cref="SearchText.FoldVersion"/>): один раз при
/// открытии базы, только для имён, которых изменение касается. Версия хранится в таблице <c>meta</c>.
/// </summary>
internal static class SearchFoldUpgrade
{
    private const string MetaKey = "search_fold_version";

    // Имена с комбинирующими знаками и разложенным хангылем — только их меняет версия 2 (свёртка NFD, этап 10.3).
    // Диапазоны GLOB в SQLite сравнивают кодовые точки.
    private const string AffectedNames = "*[\u0300-\u036F\u0483-\u0489\u1160-\u11FF\u1AB0-\u1AFF\u1DC0-\u1DFF\u20D0-\u20FF\u3099-\u309A\uFE20-\uFE2F]*";

    /// <summary>Пересчитывает свёртку, если база записана по старым правилам; возвращает число изменённых записей.</summary>
    public static int Apply(SqliteConnection connection)
    {
        var stored = connection.ExecuteScalar<string?>("SELECT value FROM meta WHERE key = @MetaKey", new { MetaKey });
        if (int.TryParse(stored, NumberStyles.None, CultureInfo.InvariantCulture, out var version) && version >= SearchText.FoldVersion)
        {
            return 0;
        }

        using var transaction = connection.BeginTransaction();
        var updated = 0;
        var rows = connection.Query<(long Id, string Name, string Folded)>(
            "SELECT id, name, name_folded FROM entries WHERE name GLOB @AffectedNames", new { AffectedNames }, transaction);
        foreach (var (id, name, folded) in rows)
        {
            var refolded = SearchText.Fold(name);
            if (!string.Equals(refolded, folded, StringComparison.Ordinal))
            {
                updated += connection.Execute("UPDATE entries SET name_folded = @Folded WHERE id = @Id", new { Folded = refolded, Id = id }, transaction);
            }
        }

        connection.Execute(
            "INSERT INTO meta (key, value) VALUES (@MetaKey, @Value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
            new { MetaKey, Value = SearchText.FoldVersion.ToString(CultureInfo.InvariantCulture) },
            transaction);
        transaction.Commit();
        return updated;
    }
}

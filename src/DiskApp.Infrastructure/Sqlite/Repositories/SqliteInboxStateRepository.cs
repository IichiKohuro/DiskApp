using Dapper;

using DiskApp.Core.Paths;
using DiskApp.Core.Repositories;
using DiskApp.Core.Sorting;

namespace DiskApp.Infrastructure.Sqlite.Repositories;

/// <summary>
/// Состояния файлов в папках-накопителях в SQLite (PLAN.md, этап 17.1): таблица <c>inbox_states</c> — полный путь,
/// состояние и время изменения. Путь в другом регистре в Windows — тот же файл, поэтому совпадения ищутся в .NET: записей
/// единицы и десятки.
/// </summary>
public sealed class SqliteInboxStateRepository(SqliteDatabase database, PathNormalizer normalizer) : SqliteRepository(database), IInboxStateRepository
{
    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, InboxItemState>> GetAllAsync(CancellationToken cancellationToken = default)
        => ReadAsync<IReadOnlyDictionary<string, InboxItemState>>(connection =>
        {
            var result = new Dictionary<string, InboxItemState>(normalizer.Comparer);
            foreach (var row in connection.Query<StateRow>("SELECT path AS Path, state AS State FROM inbox_states"))
            {
                result[row.Path] = (InboxItemState)row.State;
            }

            return result;
        }, cancellationToken);

    /// <inheritdoc />
    public Task SetAsync(IReadOnlyList<string> paths, InboxItemState state, DateTimeOffset changedUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            return Task.CompletedTask;
        }

        return WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            DeleteSame(connection, transaction, paths);
            if (state != InboxItemState.New)
            {
                foreach (var path in paths)
                {
                    connection.Execute(
                        "INSERT INTO inbox_states (path, state, changed_utc) VALUES (@Path, @State, @Changed)",
                        new { Path = path, State = (int)state, Changed = SqliteTime.Encode(changedUtc) },
                        transaction);
                }
            }

            transaction.Commit();
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count == 0)
        {
            return Task.CompletedTask;
        }

        return WriteAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            DeleteSame(connection, transaction, paths);
            transaction.Commit();
        }, cancellationToken);
    }

    // Удаляет записи этих путей, в том числе сохранённые в другом регистре.
    private void DeleteSame(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction, IReadOnlyList<string> targets)
    {
        var wanted = new HashSet<string>(targets, normalizer.Comparer);
        foreach (var stored in connection.Query<string>("SELECT path FROM inbox_states", transaction: transaction).Where(wanted.Contains).ToArray())
        {
            connection.Execute("DELETE FROM inbox_states WHERE path = @Path", new { Path = stored }, transaction);
        }
    }

    private sealed class StateRow
    {
        public string Path { get; set; } = string.Empty;

        public long State { get; set; }
    }
}

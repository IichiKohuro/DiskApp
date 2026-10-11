namespace DiskApp.Infrastructure.Sqlite;

/// <summary>
/// Настройки SQLite на весь процесс; применяются до первого соединения, позже SQLite их не принимает.
/// </summary>
internal static class SqliteGlobalConfiguration
{
    // SQLITE_CONFIG_MEMSTATUS из sqlite3.h.
    private const int ConfigMemoryStatus = 9;

    private static int _applied;

    /// <summary>
    /// Выключает учёт памяти SQLite: иначе каждое выделение памяти берёт глобальный мьютекс, и параллельные поиски
    /// почти не масштабируются (этап 4.7: с 188 до 237 поисков в секунду на 16 потоках). Статистика памяти SQLite
    /// приложению не нужна. Если соединение уже открывалось, SQLite вернёт ошибку — тогда остаётся как есть.
    /// </summary>
    public static void Apply()
    {
        if (Interlocked.Exchange(ref _applied, 1) != 0)
        {
            return;
        }

        SQLitePCL.Batteries_V2.Init();
        _ = SQLitePCL.raw.sqlite3_config(ConfigMemoryStatus, 0);
    }
}

namespace DiskApp.Core.Abstractions;

/// <summary>
/// Папки и файлы приложения (docs/ARCHITECTURE.md, раздел 4): обычный режим — <c>%LocalAppData%\DiskApp</c>,
/// портативный — <c>.\data</c> рядом с исполняемым файлом. Реализация создаёт папки при запуске (этап 1.5).
/// </summary>
public interface IAppPaths
{
    /// <summary>Портативный режим: данные рядом с исполняемым файлом.</summary>
    bool IsPortable { get; }

    /// <summary>Корневая папка данных.</summary>
    string DataDirectory { get; }

    /// <summary>Файл индекса <c>index.db</c>.</summary>
    string DatabasePath { get; }

    /// <summary>Файл настроек <c>settings.json</c>.</summary>
    string SettingsPath { get; }

    /// <summary>Журналы.</summary>
    string LogsDirectory { get; }

    /// <summary>Кэш иконок и миниатюр.</summary>
    string CacheDirectory { get; }

    /// <summary>Кэш иконок по расширению и по файлу.</summary>
    string IconCacheDirectory { get; }

    /// <summary>Кэш миниатюр.</summary>
    string ThumbnailCacheDirectory { get; }

    /// <summary>Резервные копии данных приложения по умолчанию.</summary>
    string BackupsDirectory { get; }
}

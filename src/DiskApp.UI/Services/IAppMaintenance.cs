namespace DiskApp.UI.Services;

/// <summary>Сторонний компонент приложения для раздела «О программе».</summary>
/// <param name="Name">Имя пакета.</param>
/// <param name="Version">Версия.</param>
public sealed record ThirdPartyComponent(string Name, string Version)
{
    /// <summary>Страница пакета с лицензией.</summary>
    public Uri Link => new($"https://www.nuget.org/packages/{Name}/{Version}");
}

/// <summary>
/// Обслуживание данных приложения (PLAN.md, этап 9.1, раздел «Данные»): папки, размеры индекса и кэша, пересборка
/// индекса, очистка кэша, сторонние компоненты.
/// </summary>
public interface IAppMaintenance
{
    /// <summary>Действия доступны на этой платформе.</summary>
    bool IsSupported { get; }

    /// <summary>Папка данных: база, настройки, кэш, журнал.</summary>
    string DataDirectory { get; }

    /// <summary>Папка журнала.</summary>
    string LogsDirectory { get; }

    /// <summary>Размер базы индекса с журналом WAL, байт.</summary>
    long DatabaseSize { get; }

    /// <summary>Размер кэша иконок и миниатюр, байт.</summary>
    long CacheSize { get; }

    /// <summary>Сторонние компоненты в сборке (список создаётся при сборке).</summary>
    IReadOnlyList<ThirdPartyComponent> ThirdParty { get; }

    /// <summary>Открывает папку в Проводнике.</summary>
    void OpenFolder(string path);

    /// <summary>Удаляет кэш иконок и миниатюр: на диске и в памяти. Индекс и настройки не трогаются.</summary>
    Task ClearCacheAsync();

    /// <summary>Пересобирает индекс всех корней заново.</summary>
    Task RebuildIndexAsync();

    /// <summary>
    /// Собирает диагностический архив (PLAN.md, этап 9.5): последние файлы журнала, настройки без секретов, версия и
    /// окружение.
    /// </summary>
    /// <returns>Путь к архиву.</returns>
    Task<string> CreateDiagnosticArchiveAsync();

    /// <summary>Последние строки текущего файла журнала (страница журнала, этап 9.5).</summary>
    Task<IReadOnlyList<string>> ReadLogTailAsync(int maxLines);

    /// <summary>
    /// Сбрасывает настройки к значениям по умолчанию; прежний файл сохраняется рядом (этап 9.5). Корни, теги и коллекции
    /// хранятся в базе и не меняются.
    /// </summary>
    /// <returns>Путь к копии прежних настроек или <c>null</c>, если файла не было.</returns>
    Task<string?> ResetSettingsAsync();
}

/// <summary>
/// Обслуживание недоступно (превью, тесты).
/// </summary>
public sealed class NullAppMaintenance : IAppMaintenance
{
    /// <inheritdoc />
    public bool IsSupported => false;

    /// <inheritdoc />
    public string DataDirectory => string.Empty;

    /// <inheritdoc />
    public string LogsDirectory => string.Empty;

    /// <inheritdoc />
    public long DatabaseSize => 0;

    /// <inheritdoc />
    public long CacheSize => 0;

    /// <inheritdoc />
    public IReadOnlyList<ThirdPartyComponent> ThirdParty => [];

    /// <inheritdoc />
    public void OpenFolder(string path)
    {
    }

    /// <inheritdoc />
    public Task ClearCacheAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public Task RebuildIndexAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public Task<string> CreateDiagnosticArchiveAsync() => Task.FromException<string>(new NotSupportedException());

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ReadLogTailAsync(int maxLines) => Task.FromResult<IReadOnlyList<string>>([]);

    /// <inheritdoc />
    public Task<string?> ResetSettingsAsync() => Task.FromResult<string?>(null);
}

/// <summary>Чьё сочетание проверяется.</summary>
public enum HotkeyTarget
{
    /// <summary>Быстрый поиск.</summary>
    QuickSearch,

    /// <summary>Quick Switch.</summary>
    QuickSwitch,

    /// <summary>Главное окно (PLAN.md, этап 18.1).</summary>
    MainWindow,

    /// <summary>Окно Drag&amp;Drop (PLAN.md, этап 18.1).</summary>
    DropWindow,
}

/// <summary>
/// Проверка глобального сочетания перед сохранением (PLAN.md, этап 9.1): свободно ли оно в Windows и не занято ли другой
/// функцией DiskApp.
/// </summary>
public interface IHotkeyChecker
{
    /// <summary>
    /// Проверяет сочетание.
    /// </summary>
    /// <returns>Почему сочетание нельзя взять, или <c>null</c>, если можно.</returns>
    string? Check(Hotkey hotkey, HotkeyTarget target);
}

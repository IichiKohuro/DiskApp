using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

using DiskApp.Core.Abstractions;
using DiskApp.Core.Localization;

namespace DiskApp.Infrastructure.Settings;

/// <summary>
/// Настройки в JSON-файле (PLAN.md, этап 1.5): чтение при старте с миграцией по версии схемы, повреждённый файл
/// откладывается в <c>settings.corrupt-&lt;дата&gt;.json</c>, настройки восстанавливаются из резервной копии
/// <c>settings.json.bak</c> или берутся значения по умолчанию; запись атомарная через временный файл и замену;
/// подписчики узнают о каждом сохранении.
/// </summary>
/// <typeparam name="T">Тип настроек с полем <c>schemaVersion</c>.</typeparam>
public sealed class JsonSettingsStore<T> : ISettingsStore<T>
    where T : class
{
    private const string SchemaVersionProperty = "schemaVersion";

    private readonly JsonTypeInfo<T> _typeInfo;
    private readonly Func<T> _defaults;
    private readonly int _currentSchemaVersion;
    private readonly IReadOnlyDictionary<int, Action<JsonObject>> _migrations;
    private readonly Lock _gate = new();
    private readonly List<Subscription> _subscribers = [];
    private T? _current;

    /// <summary>
    /// Создаёт хранилище; файл читается в <see cref="Load"/>.
    /// </summary>
    /// <param name="path">Путь к файлу настроек.</param>
    /// <param name="typeInfo">Метаданные сериализации из source generator.</param>
    /// <param name="defaults">Фабрика значений по умолчанию.</param>
    /// <param name="currentSchemaVersion">Версия схемы текущей сборки.</param>
    /// <param name="migrations">Миграции «с версии N»: правят JSON до чтения; версия N+1 выставляется автоматически.</param>
    public JsonSettingsStore(
        string path,
        JsonTypeInfo<T> typeInfo,
        Func<T> defaults,
        int currentSchemaVersion,
        IReadOnlyDictionary<int, Action<JsonObject>>? migrations = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(typeInfo);
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentOutOfRangeException.ThrowIfLessThan(currentSchemaVersion, 1);

        Path = System.IO.Path.GetFullPath(path);
        _typeInfo = typeInfo;
        _defaults = defaults;
        _currentSchemaVersion = currentSchemaVersion;
        _migrations = migrations ?? new Dictionary<int, Action<JsonObject>>();
    }

    /// <summary>Путь к файлу.</summary>
    public string Path { get; }

    /// <summary>Резервная копия: предыдущая исправная версия файла, из неё восстанавливается повреждённый.</summary>
    public string BackupPath => Path + ".bak";

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Файл ещё не прочитан: вызовите <see cref="Load"/>.</exception>
    public T Current => _current ?? throw new InvalidOperationException("Настройки не загружены: вызовите Load.");

    /// <summary>
    /// Читает файл. Отсутствующий файл даёт значения по умолчанию без записи на диск;
    /// старая версия мигрируется и сохраняется; повреждённый файл откладывается, а настройки восстанавливаются
    /// из резервной копии <see cref="BackupPath"/>, если она цела.
    /// </summary>
    public SettingsLoadResult Load()
    {
        if (!File.Exists(Path))
        {
            _current = _defaults();
            return new SettingsLoadResult(SettingsLoadOutcome.Defaults, null, 0);
        }

        var read = Read(Path);
        if (read.Settings is { } settings)
        {
            _current = settings;
            if (read.Migrated)
            {
                Write(settings);
                return new SettingsLoadResult(SettingsLoadOutcome.Migrated, null, read.Version);
            }

            return new SettingsLoadResult(SettingsLoadOutcome.Loaded, null, read.Version);
        }

        var corruptPath = Quarantine();
        if (File.Exists(BackupPath) && Read(BackupPath) is { Settings: { } restored } backup)
        {
            // Основной файл возвращается из копии; сама копия остаётся как есть.
            _current = restored;
            Write(restored);
            return new SettingsLoadResult(SettingsLoadOutcome.RestoredFromBackup, corruptPath, backup.Version, read.Error);
        }

        _current = _defaults();
        return new SettingsLoadResult(SettingsLoadOutcome.Corrupt, corruptPath, 0, read.Error);
    }

    /// <inheritdoc />
    public Task SaveAsync(T settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(settings);
            _current = settings;
            Notify(settings);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public IDisposable Subscribe(Action<T> onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);

        var subscription = new Subscription(this, onChanged);
        lock (_gate)
        {
            _subscribers.Add(subscription);
        }

        return subscription;
    }

    private ReadResult Read(string file)
    {
        JsonObject document;
        try
        {
            using var stream = File.OpenRead(file);
            document = JsonNode.Parse(stream) as JsonObject ?? throw new JsonException(L.T("JsonSettingsStore.TheRootOfTheSettingsFile"));
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return new ReadResult(null, 0, false, exception.Message);
        }

        try
        {
            var version = document[SchemaVersionProperty]?.GetValue<int>() ?? 0;
            var migrated = false;
            while (version < _currentSchemaVersion)
            {
                if (_migrations.TryGetValue(version, out var migrate))
                {
                    migrate(document);
                }

                version++;
                document[SchemaVersionProperty] = version;
                migrated = true;
            }

            var settings = document.Deserialize(_typeInfo) ?? throw new JsonException(L.T("JsonSettingsStore.TheSettingsFileIsEmpty"));
            return new ReadResult(settings, version, migrated, null);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return new ReadResult(null, 0, false, exception.Message);
        }
    }

    private void Write(T settings)
    {
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        Directory.CreateDirectory(directory);
        var temporary = System.IO.Path.Combine(directory, System.IO.Path.GetFileName(Path) + ".tmp");

        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, settings, _typeInfo);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(Path))
        {
            try
            {
                // Прежняя версия, прочитанная или записанная без ошибок, становится резервной копией (этап 10.3).
                File.Replace(temporary, Path, BackupPath, ignoreMetadataErrors: true);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                // Файловая система без замены файла (часть сетевых и FAT): копия, затем переименование.
                if (File.Exists(Path))
                {
                    File.Copy(Path, BackupPath, overwrite: true);
                }
            }
        }

        File.Move(temporary, Path, overwrite: true);
    }

    private void Notify(T settings)
    {
        Subscription[] subscribers;
        lock (_gate)
        {
            subscribers = [.. _subscribers];
        }

        foreach (var subscriber in subscribers)
        {
            subscriber.Handler(settings);
        }
    }

    private string Quarantine()
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        var name = System.IO.Path.GetFileNameWithoutExtension(Path);
        var extension = System.IO.Path.GetExtension(Path);
        var corruptPath = System.IO.Path.Combine(directory, $"{name}.corrupt-{stamp}{extension}");
        for (var attempt = 1; File.Exists(corruptPath); attempt++)
        {
            corruptPath = System.IO.Path.Combine(directory, $"{name}.corrupt-{stamp}-{attempt.ToString(CultureInfo.InvariantCulture)}{extension}");
        }

        File.Move(Path, corruptPath);
        return corruptPath;
    }

    private void Unsubscribe(Subscription subscription)
    {
        lock (_gate)
        {
            _subscribers.Remove(subscription);
        }
    }

    private readonly record struct ReadResult(T? Settings, int Version, bool Migrated, string? Error);

    private sealed class Subscription(JsonSettingsStore<T> store, Action<T> handler) : IDisposable
    {
        private int _disposed;

        public Action<T> Handler { get; } = handler;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                store.Unsubscribe(this);
            }
        }
    }
}

/// <summary>
/// Итог чтения файла настроек.
/// </summary>
/// <param name="Outcome">Что произошло.</param>
/// <param name="CorruptFilePath">Куда отложен повреждённый файл; <c>null</c>, если повреждения не было.</param>
/// <param name="SchemaVersion">Версия схемы прочитанного файла после миграции; 0 для значений по умолчанию.</param>
/// <param name="Error">Текст ошибки разбора; <c>null</c>, если ошибки не было.</param>
public sealed record SettingsLoadResult(SettingsLoadOutcome Outcome, string? CorruptFilePath, int SchemaVersion, string? Error = null);

/// <summary>
/// Исход чтения настроек.
/// </summary>
public enum SettingsLoadOutcome
{
    /// <summary>Файл прочитан как есть.</summary>
    Loaded,

    /// <summary>Файла не было, действуют значения по умолчанию.</summary>
    Defaults,

    /// <summary>Файл старой версии мигрирован и перезаписан.</summary>
    Migrated,

    /// <summary>Файл повреждён, отложен, действуют значения по умолчанию.</summary>
    Corrupt,

    /// <summary>Файл повреждён и отложен, настройки восстановлены из резервной копии.</summary>
    RestoredFromBackup,
}

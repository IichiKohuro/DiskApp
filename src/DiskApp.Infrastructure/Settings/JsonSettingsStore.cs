using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

using DiskApp.Core.Abstractions;

namespace DiskApp.Infrastructure.Settings;

/// <summary>
/// Настройки в JSON-файле (PLAN.md, этап 1.5): чтение при старте с миграцией по версии схемы, повреждённый файл
/// откладывается в <c>settings.corrupt-&lt;дата&gt;.json</c> и берутся значения по умолчанию; запись атомарная
/// через временный файл и переименование; подписчики узнают о каждом сохранении.
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

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Файл ещё не прочитан: вызовите <see cref="Load"/>.</exception>
    public T Current => _current ?? throw new InvalidOperationException("Настройки не загружены: вызовите Load.");

    /// <summary>
    /// Читает файл. Отсутствующий файл даёт значения по умолчанию без записи на диск;
    /// старая версия мигрируется и сохраняется; повреждённый файл откладывается.
    /// </summary>
    public SettingsLoadResult Load()
    {
        if (!File.Exists(Path))
        {
            _current = _defaults();
            return new SettingsLoadResult(SettingsLoadOutcome.Defaults, null, 0);
        }

        JsonObject document;
        try
        {
            using var stream = File.OpenRead(Path);
            document = JsonNode.Parse(stream) as JsonObject ?? throw new JsonException("Корень файла настроек должен быть объектом.");
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            var corruptPath = Quarantine();
            _current = _defaults();
            return new SettingsLoadResult(SettingsLoadOutcome.Corrupt, corruptPath, 0, exception.Message);
        }

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

        T settings;
        try
        {
            settings = document.Deserialize(_typeInfo) ?? throw new JsonException("Файл настроек пуст.");
        }
        catch (JsonException exception)
        {
            var corruptPath = Quarantine();
            _current = _defaults();
            return new SettingsLoadResult(SettingsLoadOutcome.Corrupt, corruptPath, 0, exception.Message);
        }

        _current = settings;
        if (migrated)
        {
            Write(settings);
            return new SettingsLoadResult(SettingsLoadOutcome.Migrated, null, version);
        }

        return new SettingsLoadResult(SettingsLoadOutcome.Loaded, null, version);
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
}

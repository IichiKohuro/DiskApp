using System.Text.Json;
using System.Text.Json.Nodes;

using DiskApp.Client;
using DiskApp.Contracts;
using DiskApp.Contracts.App;

namespace DiskApp.UI.Services;

/// <summary>
/// Настройки приложения для страницы «Настройки» (PLAN.md, этап 9.1): чтение и частичное изменение (JSON Merge Patch).
/// Изменение сохраняется сразу; десктоп применяет его по подписке на хранилище без перезапуска.
/// </summary>
public interface ISettingsService
{
    /// <summary>Текущие настройки.</summary>
    Task<SettingsDto> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Меняет часть настроек: <c>{"search":{"fuzzy":false}}</c>; <c>null</c> возвращает значение по умолчанию.
    /// </summary>
    /// <returns>Настройки после изменения.</returns>
    /// <exception cref="InvalidOperationException">Значение отклонено; текст объясняет почему.</exception>
    Task<SettingsDto> PatchAsync(JsonObject patch, CancellationToken cancellationToken = default);

    /// <summary>Сведения о приложении: версия, папка данных, режим запуска.</summary>
    Task<AppInfoDto> GetAppInfoAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Настройки через API сервера внутри процесса.
/// </summary>
public sealed class ApiSettingsService(IDiskAppApi api) : ISettingsService
{
    private readonly IDiskAppApi _api = api ?? throw new ArgumentNullException(nameof(api));

    /// <inheritdoc />
    public Task<SettingsDto> GetAsync(CancellationToken cancellationToken = default) => _api.GetSettingsAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<SettingsDto> PatchAsync(JsonObject patch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        try
        {
            return await _api.PatchSettingsAsync(patch, cancellationToken).ConfigureAwait(false);
        }
        catch (DiskAppApiException exception)
        {
            throw new InvalidOperationException(Describe(exception), exception);
        }
    }

    /// <inheritdoc />
    public Task<AppInfoDto> GetAppInfoAsync(CancellationToken cancellationToken = default) => _api.GetAppInfoAsync(cancellationToken);

    private static string Describe(DiskAppApiException exception)
        => exception.Errors is { Count: > 0 } errors
            ? string.Join(" ", errors.SelectMany(static pair => pair.Value))
            : exception.Message;
}

/// <summary>
/// Настройки в памяти: превью дизайнера и тесты страницы. Значения не проверяются, как на сервере, — только сливаются.
/// </summary>
public sealed class SampleSettingsService : ISettingsService
{
    private readonly Lock _gate = new();
    private SettingsDto _settings;

    /// <summary>Создаёт сервис с настройками по умолчанию, как у нового приложения.</summary>
    public SampleSettingsService()
        : this(Defaults())
    {
    }

    /// <summary>Создаёт сервис с заданными настройками.</summary>
    public SampleSettingsService(SettingsDto settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <summary>Сколько раз меняли настройки.</summary>
    public int PatchCount { get; private set; }

    /// <summary>Последнее изменение.</summary>
    public JsonObject? LastPatch { get; private set; }

    /// <summary>Отклонять изменения с этим текстом (тесты).</summary>
    public string? RejectWith { get; set; }

    /// <summary>Настройки нового приложения в форме API.</summary>
    public static SettingsDto Defaults() => new()
    {
        Hotkeys = new HotkeySettingsDto { QuickSearch = "Ctrl+Alt+D", Triage = "Ctrl+Alt+I", QuickSwitch = "Ctrl+Alt+G" },
        Appearance = new AppearanceSettingsDto { UseSystemAccent = true, FontScale = 1.0 },
        Indexing = new IndexingSettingsDto
        {
            Exclusions = [new ExclusionRuleDto(ExclusionKind.Glob, "**/node_modules/**"), new ExclusionRuleDto(ExclusionKind.Glob, "**/.git/**")],
            BatchSize = 5_000,
            WatcherDebounceMilliseconds = 400,
            ReconcileIntervalMinutes = 360,
        },
        Search = new SearchSettingsDto { PageSize = 50, CandidateLimit = 2_000, HistoryLimit = 100, FrecencyHalfLifeDays = 30, Fuzzy = true, ShowSuggestionsOnEmpty = true },
        Tray = new TraySettingsDto { StartMinimized = true, CloseToTray = true, ConfirmExitDuringOperations = true },
        Add = new AddSettingsDto { ClipboardNameTemplate = "{date}_{time}_{title}" },
        Popup = new PopupSettingsDto { HideOnDeactivate = true, ShowPath = true, ShowSize = true, ShowDate = true },
        Diagnostics = new DiagnosticsSettingsDto { LogLevel = "Information" },
    };

    /// <inheritdoc />
    public Task<SettingsDto> GetAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_settings);
        }
    }

    /// <inheritdoc />
    public Task<SettingsDto> PatchAsync(JsonObject patch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (RejectWith is { } reason)
        {
            throw new InvalidOperationException(reason);
        }

        lock (_gate)
        {
            var target = JsonSerializer.SerializeToNode(_settings, AllFields.SettingsDto)!.AsObject();
            Merge(target, patch);
            _settings = target.Deserialize(AllFields.SettingsDto)!;
            PatchCount++;
            LastPatch = patch.DeepClone().AsObject();
            return Task.FromResult(_settings);
        }
    }

    /// <inheritdoc />
    public Task<AppInfoDto> GetAppInfoAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new AppInfoDto("DiskApp", "0.0.0", ".NET", "Windows", @"C:\Users\user\AppData\Local\DiskApp", false, DateTimeOffset.UnixEpoch));

    private static DiskAppJsonContext AllFields { get; } = new(new JsonSerializerOptions(DiskAppJsonContext.Default.Options)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    });

    private static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (name, value) in patch)
        {
            if (value is JsonObject child && target[name] is JsonObject existing)
            {
                Merge(existing, child);
            }
            else
            {
                target[name] = value?.DeepClone();
            }
        }
    }
}

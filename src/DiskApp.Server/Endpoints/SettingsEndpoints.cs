using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using DiskApp.Contracts;
using DiskApp.Contracts.App;
using DiskApp.Core.Abstractions;
using DiskApp.Core.Settings;
using DiskApp.Infrastructure.Settings;
using DiskApp.Server.Hosting;
using DiskApp.Server.Localization;
using DiskApp.Server.Mapping;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace DiskApp.Server.Endpoints;

/// <summary>
/// Настройки и сведения о приложении (PLAN.md, этап 4.3). Изменение — JSON Merge Patch (RFC 7396) поверх текущих
/// настроек: переданные поля заменяются, <c>null</c> возвращает значение по умолчанию, неизвестное поле — 400.
/// </summary>
internal static partial class SettingsEndpoints
{
    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();

    private static readonly string[] UnknownSetting = [L.T("SettingsEndpoints.UnknownSetting")];

    /// <summary>
    /// Контекст сериализации со всеми полями, включая <c>null</c>: иначе поле без значения (свой акцент, папка «Входящие»)
    /// выпало бы из основы слияния и считалось бы неизвестным.
    /// </summary>
    private static readonly DiskAppJsonContext AllFields = new(new JsonSerializerOptions(DiskAppJsonContext.Default.Options) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never });

    public static void MapSettings(this IEndpointRouteBuilder endpoints)
    {
        var settings = endpoints.MapGroup(string.Empty).WithTags("Settings");

        settings.MapGet(ApiRoutes.V1.Settings, Get)
            .WithName("GetSettings")
            .WithSummary("Настройки");

        settings.MapPatch(ApiRoutes.V1.Settings, PatchAsync)
            .WithName("PatchSettings")
            .WithSummary("Изменить настройки")
            .WithDescription("Частичный JSON той же формы, что ответ GET (JSON Merge Patch): {\"search\":{\"pageSize\":100}}. "
                + "null возвращает значение по умолчанию; неизвестное поле или значение вне допустимого диапазона — 400.");

        endpoints.MapGet(ApiRoutes.V1.AppInfo, GetAppInfo)
            .WithTags("App")
            .WithName("GetAppInfo")
            .WithSummary("Сведения о приложении")
            .WithDescription("Версия, среда выполнения, ОС, папка данных и режим запуска.");
    }

    private static Ok<SettingsDto> Get(ISettingsStore<AppSettings> store) => TypedResults.Ok(store.Current.ToDto());

    private static async Task<Results<Ok<SettingsDto>, ValidationProblem>> PatchAsync(JsonObject patch, ISettingsStore<AppSettings> store, CancellationToken cancellationToken)
    {
        var current = store.Current;
        var target = JsonSerializer.SerializeToNode(current.ToDto(), AllFields.SettingsDto)!.AsObject();
        var defaults = JsonSerializer.SerializeToNode(AppSettings.Default.ToDto(), AllFields.SettingsDto)!.AsObject();

        var unknown = new List<string>();
        Merge(target, patch, defaults, string.Empty, unknown);
        if (unknown.Count > 0)
        {
            return ApiProblems.Invalid(unknown.ToDictionary(static path => path, static _ => UnknownSetting, StringComparer.Ordinal));
        }

        SettingsDto? merged;
        try
        {
            merged = target.Deserialize(DiskAppJsonContext.Default.SettingsDto);
        }
        catch (JsonException exception)
        {
            return ApiProblems.Invalid(exception.Path ?? "settings", L.T("SettingsEndpoints.ValueOfTheWrongType") + exception.Message);
        }

        if (merged is null)
        {
            return ApiProblems.Invalid("settings", L.T("SettingsEndpoints.EmptySettings"));
        }

        var errors = Validate(merged);
        if (errors.Count > 0)
        {
            return ApiProblems.Invalid(errors);
        }

        var updated = merged.ToDomain(current);
        await store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(updated.ToDto());
    }

    private static Ok<AppInfoDto> GetAppInfo(ServerState state, IAppPaths paths, ISettingsStore<AppSettings> store)
    {
        // Хранилище читает файл настроек при первом обращении: после него итог чтения уже известен.
        _ = store.Current;
        var settings = state.Settings;
        return TypedResults.Ok(new AppInfoDto(
            "DiskApp",
            state.Version,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            paths.DataDirectory,
            paths.IsPortable,
            state.StartedUtc,
            state.IsDatabaseReady ? state.Database.Result.CorruptFilePath : null,
            settings?.CorruptFilePath,
            settings?.Outcome == SettingsLoadOutcome.RestoredFromBackup));
    }

    /// <summary>
    /// JSON Merge Patch с возвратом значений по умолчанию для <c>null</c>; имена сопоставляются без учёта регистра.
    /// </summary>
    internal static void Merge(JsonObject target, JsonObject patch, JsonObject? defaults, string path, List<string> unknown)
    {
        foreach (var (name, value) in patch)
        {
            var key = target.Select(static pair => pair.Key).FirstOrDefault(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase));
            var fieldPath = path.Length == 0 ? name : path + "." + name;
            if (key is null)
            {
                unknown.Add(fieldPath);
                continue;
            }

            if (value is null)
            {
                target[key] = defaults?[key]?.DeepClone();
            }
            else if (value is JsonObject patchObject && target[key] is JsonObject targetObject)
            {
                Merge(targetObject, patchObject, defaults?[key] as JsonObject, fieldPath, unknown);
            }
            else
            {
                target[key] = value.DeepClone();
            }
        }
    }

    private static Dictionary<string, string[]> Validate(SettingsDto settings)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        void Range(string name, int value, int min, int max)
        {
            if (value < min || value > max)
            {
                errors[name] = [L.F("SettingsEndpoints.AllowedFromTo", min, max)];
            }
        }

        Range("search.pageSize", settings.Search.PageSize, 10, 500);
        Range("search.candidateLimit", settings.Search.CandidateLimit, 100, 5_000);
        Range("search.historyLimit", settings.Search.HistoryLimit, 0, 1_000);
        Range("search.frecencyHalfLifeDays", settings.Search.FrecencyHalfLifeDays, 1, 365);
        Range("indexing.batchSize", settings.Indexing.BatchSize, 100, 50_000);
        Range("indexing.watcherDebounceMilliseconds", settings.Indexing.WatcherDebounceMilliseconds, 50, 2_000);
        Range("indexing.reconcileIntervalMinutes", settings.Indexing.ReconcileIntervalMinutes, 0, 10_080);
        Range("indexing.maxScanParallelism", settings.Indexing.MaxScanParallelism, 0, 64);

        if (string.IsNullOrWhiteSpace(settings.Hotkeys.QuickSearch))
        {
            errors["hotkeys.quickSearch"] = [L.T("Settings.ShortcutEmpty")];
        }

        if (string.IsNullOrWhiteSpace(settings.Hotkeys.QuickSwitch))
        {
            errors["hotkeys.quickSwitch"] = [L.T("Settings.ShortcutEmpty")];
        }

        if (settings.Appearance.FontScale is < 0.9 or > 1.5 || double.IsNaN(settings.Appearance.FontScale))
        {
            errors["appearance.fontScale"] = [L.T("SettingsEndpoints.AllowedFrom09To1")];
        }

        if (settings.Appearance.AccentColor is { } accent && !HexColor().IsMatch(accent))
        {
            errors["appearance.accentColor"] = [L.T("SettingsEndpoints.AColorLikeRRGGBBIsRequired")];
        }

        if (settings.Appearance.Language is not ("system" or "ru" or "en"))
        {
            errors["appearance.language"] = [L.T("SettingsEndpoints.UnknownLanguage")];
        }

        if (!Enum.IsDefined(settings.Popup.Monitor))
        {
            errors["popup.monitor"] = [L.T("SettingsEndpoints.UnknownMonitor")];
        }

        if (!DiagnosticsSettings.LogLevels.Contains(settings.Diagnostics.LogLevel, StringComparer.Ordinal))
        {
            errors["diagnostics.logLevel"] = [L.T("SettingsEndpoints.Allowed") + string.Join(", ", DiagnosticsSettings.LogLevels) + "."];
        }

        if (string.IsNullOrWhiteSpace(settings.Add.ClipboardNameTemplate))
        {
            errors["add.clipboardNameTemplate"] = [L.T("Settings.TemplateEmpty")];
        }

        if (settings.Add.InboxPath is { Length: > 0 } inbox && !Path.IsPathFullyQualified(inbox))
        {
            errors["add.inboxPath"] = [L.T("SettingsEndpoints.AFullFolderPathIsRequired")];
        }

        if (settings.Add.RecentTargets.Count > AddSettings.RecentLimit)
        {
            errors["add.recentTargets"] = [L.F("SettingsEndpoints.NoMoreThanPlaces", AddSettings.RecentLimit)];
        }

        ValidateSortingFolders(settings.Sorting.Inboxes, "sorting.inboxes", errors);
        ValidateSortingFolders(settings.Sorting.WorkFolders, "sorting.workFolders", errors);

        for (var i = 0; i < settings.Indexing.Exclusions.Count; i++)
        {
            var rule = settings.Indexing.Exclusions[i];
            var invalid = rule.Kind switch
            {
                ExclusionKind.Glob => string.IsNullOrWhiteSpace(rule.Pattern),
                ExclusionKind.Size or ExclusionKind.Depth => !long.TryParse(rule.Pattern, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _),
                _ => false,
            };

            if (invalid)
            {
                errors[$"indexing.exclusions[{i}].pattern"] = [rule.Kind == ExclusionKind.Glob ? L.T("SettingsEndpoints.ThePatternCannotBeEmpty") : L.T("SettingsEndpoints.ANonNegativeIntegerIsRequired")];
            }
        }

        return errors;
    }

    // Папки разбора (этап 17): полные пути без повторов, не больше SortingSettings.MaxFolders в списке.
    private static void ValidateSortingFolders(IReadOnlyList<SortingFolderDto>? folders, string field, Dictionary<string, string[]> errors)
    {
        folders ??= [];
        if (folders.Count > SortingSettings.MaxFolders)
        {
            errors[field] = [L.F("SettingsEndpoints.NoMoreThanFolders", SortingSettings.MaxFolders)];
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < folders.Count; i++)
        {
            var path = folders[i].Path;
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            {
                errors[$"{field}[{i}].path"] = [L.T("SettingsEndpoints.AFullFolderPathIsRequired")];
            }
            else if (!seen.Add(path.TrimEnd('\\', '/')))
            {
                errors[$"{field}[{i}].path"] = [L.T("SettingsEndpoints.FolderListedTwice")];
            }
        }
    }
}

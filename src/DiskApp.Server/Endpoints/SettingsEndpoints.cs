using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

using DiskApp.Contracts;
using DiskApp.Contracts.App;
using DiskApp.Core.Abstractions;
using DiskApp.Core.Settings;
using DiskApp.Server.Hosting;
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
internal static class SettingsEndpoints
{
    private static readonly string[] UnknownSetting = ["Неизвестная настройка."];

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
        var target = JsonSerializer.SerializeToNode(current.ToDto(), DiskAppJsonContext.Default.SettingsDto)!.AsObject();
        var defaults = JsonSerializer.SerializeToNode(AppSettings.Default.ToDto(), DiskAppJsonContext.Default.SettingsDto)!.AsObject();

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
            return ApiProblems.Invalid(exception.Path ?? "settings", "Значение не того типа: " + exception.Message);
        }

        if (merged is null)
        {
            return ApiProblems.Invalid("settings", "Пустые настройки.");
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

    private static Ok<AppInfoDto> GetAppInfo(ServerState state, IAppPaths paths)
        => TypedResults.Ok(new AppInfoDto(
            "DiskApp",
            state.Version,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            paths.DataDirectory,
            paths.IsPortable,
            state.StartedUtc));

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
                errors[name] = [$"Допустимо от {min} до {max}."];
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
            errors["hotkeys.quickSearch"] = ["Сочетание не может быть пустым."];
        }

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
                errors[$"indexing.exclusions[{i}].pattern"] = [rule.Kind == ExclusionKind.Glob ? "Шаблон не может быть пустым." : "Нужно целое неотрицательное число."];
            }
        }

        return errors;
    }
}

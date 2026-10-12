using System.Text.Json.Nodes;

using DiskApp.Client;
using DiskApp.Contracts.App;
using DiskApp.UI.Text;

namespace DiskApp.UI.Services;

/// <summary>Папка разбора: накопитель или рабочая папка.</summary>
/// <param name="Path">Полный путь.</param>
/// <param name="Label">Подпись; <c>null</c> — имя папки.</param>
/// <param name="IncludeSubfolders">У накопителя: в очередь попадают и файлы из подпапок (этап 17.1).</param>
public sealed record SortingFolderInfo(string Path, string? Label = null, bool IncludeSubfolders = false)
{
    /// <summary>У рабочей папки: шаблоны имён для авторазбора (этап 17.5).</summary>
    public IReadOnlyList<SortingRuleInfo> Rules { get; init; } = [];

    /// <summary>Что показывать: подпись или имя папки.</summary>
    public string Title => string.IsNullOrWhiteSpace(Label) ? PathText.FileName(Path) : Label;

    /// <summary>Равенство по содержимому: шаблоны сравниваются поэлементно.</summary>
    public bool Equals(SortingFolderInfo? other)
        => other is not null && string.Equals(Path, other.Path, StringComparison.Ordinal) && string.Equals(Label, other.Label, StringComparison.Ordinal)
            && IncludeSubfolders == other.IncludeSubfolders && Rules.SequenceEqual(other.Rules);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Path, Label, IncludeSubfolders, Rules.Count);

    /// <inheritdoc />
    public override string ToString() => Title;
}

/// <summary>
/// Шаблон имени рабочей папки (PLAN.md, этап 17.5): маска имени (<c>417_*</c>; без <c>*</c> и <c>?</c> — где угодно в
/// имени) и необязательные условия.
/// </summary>
/// <param name="Pattern">Маска имени файла.</param>
/// <param name="Auto">Переносить самим; иначе — только предлагать папку первой.</param>
/// <param name="Enabled">Шаблон действует.</param>
/// <param name="Inbox">Только для файлов из этого накопителя; <c>null</c> — из любого.</param>
/// <param name="Subfolder">Подпапка рабочей папки, куда класть; <c>null</c> — в саму папку.</param>
public sealed record SortingRuleInfo(string Pattern, bool Auto = true, bool Enabled = true, string? Inbox = null, string? Subfolder = null)
{
    /// <summary>Только для этих расширений, без точки; пусто — для любых.</summary>
    public IReadOnlyList<string> Extensions { get; init; } = [];

    /// <summary>Равенство по содержимому: расширения сравниваются поэлементно.</summary>
    public bool Equals(SortingRuleInfo? other)
        => other is not null && string.Equals(Pattern, other.Pattern, StringComparison.Ordinal) && Auto == other.Auto && Enabled == other.Enabled
            && string.Equals(Inbox, other.Inbox, StringComparison.Ordinal) && string.Equals(Subfolder, other.Subfolder, StringComparison.Ordinal)
            && Extensions.SequenceEqual(other.Extensions, StringComparer.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Pattern, Auto, Enabled, Inbox, Subfolder, Extensions.Count);
}

/// <summary>Накопители и рабочие папки в порядке показа.</summary>
/// <param name="Inboxes">Накопители — куда сыплются файлы.</param>
/// <param name="WorkFolders">Рабочие папки — куда их раскладывают; первые девять — на клавишах 1–9.</param>
/// <param name="NameTemplate">Запомненный шаблон имени при переносе; <c>null</c> — имена не меняются.</param>
/// <param name="MainInbox">Путь основной папки-накопителя (этап 18.2); <c>null</c> — первая в списке.</param>
public sealed record SortingFolders(IReadOnlyList<SortingFolderInfo> Inboxes, IReadOnlyList<SortingFolderInfo> WorkFolders, string? NameTemplate = null, string? MainInbox = null)
{
    /// <summary>Ни накопителей, ни рабочих папок.</summary>
    public static SortingFolders Empty { get; } = new([], []);

    /// <summary>
    /// Действующая основная папка-накопитель: выбранная, если она ещё в списке, иначе первая; <c>null</c> — накопителей нет.
    /// Сюда падают файлы из окна Drag&amp;Drop.
    /// </summary>
    public SortingFolderInfo? Main
        => Inboxes.FirstOrDefault(folder => string.Equals(folder.Path, MainInbox, StringComparison.OrdinalIgnoreCase)) ?? (Inboxes.Count > 0 ? Inboxes[0] : null);
}

/// <summary>
/// Накопители и рабочие папки разбора (PLAN.md, этап 17): хранятся в настройках приложения.
/// </summary>
public interface ISortingSettings
{
    /// <summary>Текущие списки.</summary>
    Task<SortingFolders> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Сохраняет списки целиком.</summary>
    Task SaveAsync(SortingFolders folders, CancellationToken cancellationToken = default);
}

/// <summary>
/// Списки разбора в настройках сервера: чтение — <c>GET /settings</c>, запись — JSON Merge Patch раздела <c>sorting</c>.
/// </summary>
public sealed class ApiSortingSettings(IDiskAppApi api) : ISortingSettings
{
    private readonly IDiskAppApi _api = api ?? throw new ArgumentNullException(nameof(api));

    /// <inheritdoc />
    public async Task<SortingFolders> GetAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _api.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        return new SortingFolders(
            [.. settings.Sorting.Inboxes.Select(static folder => new SortingFolderInfo(folder.Path, folder.Label, folder.IncludeSubfolders))],
            [.. settings.Sorting.WorkFolders.Select(static folder => new SortingFolderInfo(folder.Path, folder.Label) { Rules = [.. folder.Rules.Select(ToInfo)] })],
            settings.Sorting.NameTemplate,
            settings.Sorting.MainInbox);
    }

    /// <inheritdoc />
    public Task SaveAsync(SortingFolders folders, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var patch = new JsonObject
        {
            ["sorting"] = new JsonObject
            {
                ["inboxes"] = ToJson(folders.Inboxes),
                ["workFolders"] = ToJson(folders.WorkFolders, withRules: true),
                ["nameTemplate"] = folders.NameTemplate,
                ["mainInbox"] = folders.MainInbox,
            },
        };
        return _api.PatchSettingsAsync(patch, cancellationToken);
    }

    /// <summary>Шаблон из DTO настроек.</summary>
    public static SortingRuleInfo ToInfo(SortingRuleDto rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return new SortingRuleInfo(rule.Pattern, rule.Mode == SortingRuleMode.Auto, rule.Enabled, rule.Inbox, rule.Subfolder) { Extensions = rule.Extensions ?? [] };
    }

    /// <summary>Рабочая папка с шаблонами в DTO — для сухого прогона несохранённой карточки.</summary>
    public static SortingFolderDto ToDto(SortingFolderInfo folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return new SortingFolderDto
        {
            Path = folder.Path,
            Label = folder.Label,
            Rules = [.. folder.Rules.Select(static rule => new SortingRuleDto
            {
                Pattern = rule.Pattern,
                Mode = rule.Auto ? SortingRuleMode.Auto : SortingRuleMode.Suggest,
                Enabled = rule.Enabled,
                Inbox = rule.Inbox,
                Extensions = rule.Extensions,
                Subfolder = rule.Subfolder,
            })],
        };
    }

    // Шаблоны пишутся только у рабочих папок: список заменяется целиком, и без них сохранение стёрло бы их.
    private static JsonArray ToJson(IReadOnlyList<SortingFolderInfo> folders, bool withRules = false)
        => [.. folders.Select(folder =>
        {
            var json = new JsonObject { ["path"] = folder.Path, ["label"] = folder.Label, ["includeSubfolders"] = folder.IncludeSubfolders };
            if (withRules)
            {
                json["rules"] = new JsonArray([.. folder.Rules.Select(static rule => (JsonNode)new JsonObject
                {
                    ["pattern"] = rule.Pattern,
                    ["mode"] = rule.Auto ? "Auto" : "Suggest",
                    ["enabled"] = rule.Enabled,
                    ["inbox"] = rule.Inbox,
                    ["extensions"] = new JsonArray([.. rule.Extensions.Select(static extension => (JsonNode)JsonValue.Create(extension))]),
                    ["subfolder"] = rule.Subfolder,
                })]);
            }

            return (JsonNode)json;
        })];
}

/// <summary>
/// Списки разбора в памяти: превью и тесты.
/// </summary>
public sealed class SampleSortingSettings : ISortingSettings
{
    private SortingFolders _folders;

    /// <summary>Создаёт списки; по умолчанию — макетные «Загрузки» и рабочие папки.</summary>
    public SampleSortingSettings(SortingFolders? folders = null)
    {
        _folders = folders ?? new SortingFolders(
            [new SortingFolderInfo(@"C:\Users\user\Downloads", "Загрузки"), new SortingFolderInfo(@"C:\Users\user\Documents\My Skype Received Files", "Skype")],
            [new SortingFolderInfo(@"D:\Work\TCI2"), new SortingFolderInfo(@"D:\Work\417"), new SortingFolderInfo(@"D:\Soft")]);
    }

    /// <summary>Сколько раз списки сохранены.</summary>
    public int SaveCount { get; private set; }

    /// <inheritdoc />
    public Task<SortingFolders> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(_folders);

    /// <inheritdoc />
    public Task SaveAsync(SortingFolders folders, CancellationToken cancellationToken = default)
    {
        _folders = folders ?? throw new ArgumentNullException(nameof(folders));
        SaveCount++;
        return Task.CompletedTask;
    }
}

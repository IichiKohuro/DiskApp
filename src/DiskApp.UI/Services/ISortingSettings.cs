using System.Text.Json.Nodes;

using DiskApp.Client;
using DiskApp.UI.Text;

namespace DiskApp.UI.Services;

/// <summary>Папка разбора: накопитель или рабочая папка.</summary>
/// <param name="Path">Полный путь.</param>
/// <param name="Label">Подпись; <c>null</c> — имя папки.</param>
public sealed record SortingFolderInfo(string Path, string? Label = null)
{
    /// <summary>Что показывать: подпись или имя папки.</summary>
    public string Title => string.IsNullOrWhiteSpace(Label) ? PathText.FileName(Path) : Label;

    /// <inheritdoc />
    public override string ToString() => Title;
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
            [.. settings.Sorting.Inboxes.Select(static folder => new SortingFolderInfo(folder.Path, folder.Label))],
            [.. settings.Sorting.WorkFolders.Select(static folder => new SortingFolderInfo(folder.Path, folder.Label))],
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
                ["workFolders"] = ToJson(folders.WorkFolders),
                ["nameTemplate"] = folders.NameTemplate,
                ["mainInbox"] = folders.MainInbox,
            },
        };
        return _api.PatchSettingsAsync(patch, cancellationToken);
    }

    private static JsonArray ToJson(IReadOnlyList<SortingFolderInfo> folders)
        => [.. folders.Select(static folder => (JsonNode)new JsonObject { ["path"] = folder.Path, ["label"] = folder.Label })];
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

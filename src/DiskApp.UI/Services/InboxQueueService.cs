using DiskApp.Client;
using DiskApp.Contracts.App;
using DiskApp.Contracts.Operations;
using DiskApp.UI.Localization;
using DiskApp.UI.Models;

namespace DiskApp.UI.Services;

/// <summary>Состояние файла в папке-накопителе (PLAN.md, этап 17.1).</summary>
public enum InboxItemState
{
    /// <summary>Ждёт разбора.</summary>
    New,

    /// <summary>Отложен: в конце списка, не считается в бейдже.</summary>
    Postponed,

    /// <summary>«Оставить здесь»: больше не предлагается.</summary>
    Kept,
}

/// <summary>Что подсказывают шаблоны рабочих папок (этап 17.5).</summary>
public enum SortingHint
{
    /// <summary>Ни один шаблон не подошёл.</summary>
    None,

    /// <summary>Подошла одна папка — она предлагается первой.</summary>
    Suggest,

    /// <summary>Подошла одна папка с «переносить самим» — файл уйдёт туда сам.</summary>
    Auto,

    /// <summary>Подошли разные папки — выбирает пользователь.</summary>
    Choice,
}

/// <summary>Подошедший шаблон рабочей папки.</summary>
/// <param name="WorkFolder">Путь рабочей папки.</param>
/// <param name="Destination">Куда класть: папка или подпапка из шаблона.</param>
/// <param name="Pattern">Маска шаблона.</param>
/// <param name="Auto">Шаблон «переносить самим».</param>
public sealed record SortingMatchInfo(string WorkFolder, string Destination, string Pattern, bool Auto);

/// <summary>Почему подсказана папка по истории решений (этап 17.4).</summary>
public enum SortingSuggestionKind
{
    /// <summary>Туда уже отправляли файлы с этим словом в имени.</summary>
    Word,

    /// <summary>Туда уже отправляли файлы с этим расширением.</summary>
    Extension,

    /// <summary>Туда недавно переносили из этого накопителя.</summary>
    Recent,
}

/// <summary>Подсказка рабочей папки по истории решений (этап 17.4), если шаблоны молчат.</summary>
/// <param name="Destination">Куда: папка или подпапка.</param>
/// <param name="Kind">Почему.</param>
/// <param name="Count">Сколько файлов с этим признаком уже отправлено туда; для «недавно» — сколько переносов из накопителя.</param>
/// <param name="Feature">Слово, расширение без точки или, для «недавно», путь накопителя.</param>
public sealed record SortingSuggestionInfo(string Destination, SortingSuggestionKind Kind, int Count, string Feature);

/// <summary>Файл или папка в накопителе.</summary>
/// <param name="Entry">Запись.</param>
/// <param name="Inbox">Путь накопителя из настроек.</param>
/// <param name="State">Состояние.</param>
/// <param name="IsReady">Пришёл: не меняется дольше «времени успокоения».</param>
/// <param name="Hint">Что подсказывают шаблоны рабочих папок (этап 17.5).</param>
public sealed record InboxEntry(FileEntry Entry, string Inbox, InboxItemState State, bool IsReady, SortingHint Hint = SortingHint.None)
{
    /// <summary>Подошедшие шаблоны.</summary>
    public IReadOnlyList<SortingMatchInfo> Matches { get; init; } = [];

    /// <summary>Подсказка по истории решений (этап 17.4); <c>null</c> — нет.</summary>
    public SortingSuggestionInfo? Suggestion { get; init; }
}

/// <summary>Файл накопителя в сухом прогоне шаблонов (этап 17.5).</summary>
/// <param name="Path">Полный путь.</param>
/// <param name="Name">Имя.</param>
/// <param name="Hint">Что подсказывают шаблоны.</param>
/// <param name="WillMove">Авторазбор перенесёт файл сам.</param>
/// <param name="Matches">Подошедшие шаблоны.</param>
/// <param name="NewName">Имя после переноса; <c>null</c> — прежнее.</param>
public sealed record SortingPreviewInfo(string Path, string Name, SortingHint Hint, bool WillMove, IReadOnlyList<SortingMatchInfo> Matches, string? NewName);

/// <summary>Накопитель в очереди.</summary>
/// <param name="Path">Путь из настроек.</param>
/// <param name="Problem">Почему не прочитан — для подписи; <c>null</c> — всё в порядке.</param>
public sealed record InboxFolderState(string Path, string? Problem);

/// <summary>Очередь накопителей.</summary>
/// <param name="Folders">Накопители по порядку настроек.</param>
/// <param name="Items">Файлы, новые сверху; ещё дописываемые — с <see cref="InboxEntry.IsReady"/> = <c>false</c>.</param>
/// <param name="NextReadyUtc">Когда придёт следующий дописываемый файл.</param>
public sealed record InboxSnapshotInfo(IReadOnlyList<InboxFolderState> Folders, IReadOnlyList<InboxEntry> Items, DateTimeOffset? NextReadyUtc);

/// <summary>
/// Очередь папок-накопителей (PLAN.md, этап 17.1): что пришло, что отложено и что оставлено; сообщает, когда в накопителях
/// что-то изменилось мимо интерфейса — скачалось, переименовано в Проводнике.
/// </summary>
public interface IInboxService
{
    /// <summary>Изменились файлы в корнях накопителей; событие может прийти не в UI-потоке.</summary>
    event EventHandler? Changed;

    /// <summary>Читает очередь.</summary>
    Task<InboxSnapshotInfo> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Отложить, оставить здесь или вернуть в новые.</summary>
    Task SetStateAsync(IReadOnlyList<string> paths, InboxItemState state, CancellationToken cancellationToken = default);

    /// <summary>
    /// Сухой прогон шаблонов (этап 17.5): что куда ушло бы из накопителей сейчас при этих рабочих папках; ничего не переносит.
    /// </summary>
    Task<IReadOnlyList<SortingPreviewInfo>> PreviewRulesAsync(IReadOnlyList<SortingFolderInfo> workFolders, CancellationToken cancellationToken = default);
}

/// <summary>
/// Очередь с сервера: <c>GET /sorting/inbox</c>; об изменениях сообщает хаб — клиент подписывается на корни накопителей.
/// </summary>
public sealed class ApiInboxService : IInboxService
{
    private readonly IDiskAppApi _api;
    private readonly DiskAppHubConnection? _hub;
    private readonly Lock _gate = new();
    private HashSet<long> _roots = [];

    /// <summary>Создаёт сервис.</summary>
    public ApiInboxService(IDiskAppApi api, DiskAppHubConnection? hub = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _hub = hub;
        if (hub is not null)
        {
            hub.EntriesChanged += OnEntriesChanged;
        }
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public async Task<InboxSnapshotInfo> GetAsync(CancellationToken cancellationToken = default)
    {
        var queue = await _api.GetInboxAsync(cancellationToken).ConfigureAwait(false);
        var roots = (await _api.GetRootsAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(static root => root.Id);
        await SubscribeAsync([.. queue.Folders.Select(static folder => folder.RootId).OfType<long>()], cancellationToken).ConfigureAwait(false);

        var items = queue.Items.Select(item =>
        {
            var root = roots.GetValueOrDefault(item.Entry.RootId);
            var entry = ApiSearchService.ToEntry(item.Entry, root?.Label ?? string.Empty, root?.Status == Contracts.Roots.RootStatus.Offline);
            return new InboxEntry(entry, item.Inbox, (InboxItemState)(int)item.State, item.IsReady, (SortingHint)(int)item.Decision)
            {
                Matches = [.. (item.Matches ?? []).Select(ToInfo)],
                Suggestion = item.Suggestion is { } suggestion ? new SortingSuggestionInfo(suggestion.Destination, (SortingSuggestionKind)(int)suggestion.Reason, suggestion.Count, suggestion.Feature) : null,
            };
        }).ToArray();
        var folders = queue.Folders.Select(static folder => new InboxFolderState(folder.Path, Describe(folder.Problem))).ToArray();
        return new InboxSnapshotInfo(folders, items, queue.NextReadyUtc);
    }

    /// <inheritdoc />
    public Task SetStateAsync(IReadOnlyList<string> paths, InboxItemState state, CancellationToken cancellationToken = default)
        => _api.SetInboxStateAsync(new SetInboxStateRequest { Paths = paths, State = (InboxState)(int)state }, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<SortingPreviewInfo>> PreviewRulesAsync(IReadOnlyList<SortingFolderInfo> workFolders, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workFolders);
        var preview = await _api.PreviewSortingRulesAsync(new SortingPreviewRequest { WorkFolders = [.. workFolders.Select(ApiSortingSettings.ToDto)] }, cancellationToken).ConfigureAwait(false);
        return [.. preview.Items.Select(static item => new SortingPreviewInfo(item.Path, item.Name, (SortingHint)(int)item.Decision, item.WillMove, [.. item.Matches.Select(ToInfo)], item.NewName))];
    }

    private static SortingMatchInfo ToInfo(SortingMatchDto match) => new(match.WorkFolder, match.Destination, match.Pattern, match.Mode == SortingRuleMode.Auto);

    private static string? Describe(InboxProblem problem) => problem switch
    {
        InboxProblem.OutsideIndex => L.T("Sorting.InboxOutsideIndex"),
        InboxProblem.Offline => L.T("Sorting.InboxOffline"),
        InboxProblem.Missing => L.T("Sorting.InboxMissing"),
        _ => null,
    };

    private async Task SubscribeAsync(IReadOnlyCollection<long> rootIds, CancellationToken cancellationToken)
    {
        if (_hub is null)
        {
            return;
        }

        long[] added;
        lock (_gate)
        {
            added = [.. rootIds.Where(id => !_roots.Contains(id))];
            _roots = [.. rootIds];
        }

        foreach (var rootId in added)
        {
            try
            {
                await _hub.SubscribeRootAsync(rootId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Без подписки очередь просто обновляется при входе на страницу и по кнопке.
                _ = exception;
            }
        }
    }

    private void OnEntriesChanged(Contracts.Entries.EntriesChangedDto changed)
    {
        bool ours;
        lock (_gate)
        {
            ours = _roots.Contains(changed.RootId);
        }

        if (ours)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>
/// Очередь по листингу папок (<see cref="IBrowseService"/>): превью, тесты и запасной путь без сервера. «Время успокоения» —
/// 5 секунд от времени изменения, состояния — в памяти.
/// </summary>
public sealed class BrowseInboxService(IBrowseService browse, ISortingSettings settings, Func<DateTimeOffset>? clock = null) : IInboxService
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);
    private static readonly string[] IgnoredSuffixes = [".crdownload", ".part", ".partial", ".download", ".tmp"];

    private readonly Dictionary<string, InboxItemState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _clock = clock ?? (static () => DateTimeOffset.Now);

    /// <inheritdoc />
    public event EventHandler? Changed
    {
        // Листинг не следит за папками: страница обновляется сама после каждого действия.
        add { }
        remove { }
    }

    /// <inheritdoc />
    public async Task<InboxSnapshotInfo> GetAsync(CancellationToken cancellationToken = default)
    {
        var folders = await settings.GetAsync(cancellationToken).ConfigureAwait(false);
        var now = _clock();
        var states = new List<InboxFolderState>();
        var items = new List<InboxEntry>();
        foreach (var inbox in folders.Inboxes)
        {
            try
            {
                var listing = await browse.ListAsync(inbox.Path, BrowseSort.Modified, descending: true, cancellationToken).ConfigureAwait(false);
                foreach (var entry in listing.Items.Where(static entry => !IsIgnored(entry.Name)))
                {
                    items.Add(new InboxEntry(entry, inbox.Path, _states.GetValueOrDefault(entry.FullPath), entry.Modified + Settle <= now));
                }

                states.Add(new InboxFolderState(inbox.Path, null));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                states.Add(new InboxFolderState(inbox.Path, exception.Message));
            }
        }

        items.Sort(static (left, right) => right.Entry.Modified.CompareTo(left.Entry.Modified));
        var next = items.Where(static item => !item.IsReady).Select(static item => (DateTimeOffset?)(item.Entry.Modified + Settle).ToUniversalTime()).Min();
        return new InboxSnapshotInfo(states, items, next);
    }

    /// <inheritdoc />
    public Task SetStateAsync(IReadOnlyList<string> paths, InboxItemState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var path in paths)
        {
            if (state == InboxItemState.New)
            {
                _states.Remove(path);
            }
            else
            {
                _states[path] = state;
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SortingPreviewInfo>> PreviewRulesAsync(IReadOnlyList<SortingFolderInfo> workFolders, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<SortingPreviewInfo>>([]);

    private static bool IsIgnored(string name)
        => name.StartsWith("~$", StringComparison.Ordinal) || IgnoredSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase);
}

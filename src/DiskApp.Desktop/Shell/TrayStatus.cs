using System.Globalization;

using DiskApp.Contracts.Hub;
using DiskApp.Contracts.Roots;
using DiskApp.UI.Localization;

namespace DiskApp.Desktop.Shell;

/// <summary>Состояние для иконки трея.</summary>
internal enum TrayState
{
    /// <summary>Сервер ещё запускается.</summary>
    Starting,

    /// <summary>Индекс актуален.</summary>
    Idle,

    /// <summary>Идёт индексация.</summary>
    Indexing,

    /// <summary>Индексация на паузе.</summary>
    Paused,

    /// <summary>Часть корней недоступна.</summary>
    Offline,

    /// <summary>Ошибка индексации или сервер не запустился.</summary>
    Error,
}

/// <summary>
/// Состояние трея по событиям хаба (PLAN.md, этап 6.3): что показать в подсказке и какой значок наложить на иконку.
/// Ход индексации приходит отдельно от состояния корней и копится по корням.
/// </summary>
internal sealed class TrayStatus
{
    private readonly Dictionary<long, RootIndexStatusDto> _roots = [];
    private readonly Dictionary<long, IndexProgressDto> _progress = [];
    private IndexerState _state = IndexerState.Idle;
    private bool _paused;
    private bool _ready;
    private string? _failure;

    /// <summary>Текущее состояние значка.</summary>
    public TrayState State
        => _failure is not null ? TrayState.Error
            : !_ready ? TrayState.Starting
            : _state switch
            {
                IndexerState.Error => TrayState.Error,
                IndexerState.Paused => TrayState.Paused,
                IndexerState.Indexing => TrayState.Indexing,
                IndexerState.PartiallyOffline => TrayState.Offline,
                _ => TrayState.Idle,
            };

    /// <summary>Индексация на паузе: пункт меню предлагает возобновить.</summary>
    public bool IsPaused => _paused;

    /// <summary>Подсказка иконки трея (Windows обрезает её до 127 символов).</summary>
    public string ToolTip
    {
        get
        {
            var text = State switch
            {
                TrayState.Starting => L.T("TrayStatus.DiskAppStarting"),
                TrayState.Error when _failure is not null => L.T("TrayStatus.DiskAppTheServerDidNotStart"),
                TrayState.Error => L.T("TrayStatus.DiskAppIndexingError") + string.Join(", ", _roots.Values.Where(static root => root.Status == RootStatus.Error).Select(static root => root.Label)),
                TrayState.Paused => L.T("TrayStatus.DiskAppIndexingPaused"),
                TrayState.Indexing => L.T("TrayStatus.DiskAppIndexing") + IndexingDetails(),
                TrayState.Offline => L.T("TrayStatus.DiskAppUnavailable") + string.Join(", ", _roots.Values.Where(static root => root.Status == RootStatus.Offline).Select(static root => root.Label)),
                _ => L.T("TrayStatus.DiskAppIndexIsUpToDate"),
            };
            return text.Length <= 127 ? text : text[..126] + "…";
        }
    }

    /// <summary>Полное состояние: при подключении к хабу.</summary>
    public void Apply(HubStatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);

        _ready = true;
        _failure = null;
        _state = status.Index.State;
        _paused = status.Index.IsPaused;
        _roots.Clear();
        foreach (var root in status.Index.Roots)
        {
            _roots[root.RootId] = root;
            if (root.Progress is { } progress)
            {
                _progress[root.RootId] = progress;
            }
        }
    }

    /// <summary>Состояние после команды паузы или возобновления.</summary>
    public void Apply(IndexStatusDto status)
    {
        ArgumentNullException.ThrowIfNull(status);
        Apply(new HubStatusDto(status, []));
    }

    /// <summary>Смена состояния корня.</summary>
    public void Apply(RootStatusChangedDto change)
    {
        ArgumentNullException.ThrowIfNull(change);

        if (_roots.TryGetValue(change.RootId, out var root))
        {
            _roots[change.RootId] = root with { Status = change.Status, Message = change.Message };
        }

        _state = Aggregate();
    }

    /// <summary>Ход индексации корня.</summary>
    public void Apply(IndexProgressDto progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        _progress[progress.RootId] = progress;
    }

    /// <summary>Сервер не запустился.</summary>
    public void Fail(string reason) => _failure = reason;

    private IndexerState Aggregate()
    {
        if (_roots.Values.Any(static root => root.Status == RootStatus.Error))
        {
            return IndexerState.Error;
        }

        if (_paused)
        {
            return IndexerState.Paused;
        }

        if (_roots.Values.Any(static root => root.Status == RootStatus.Scanning))
        {
            return IndexerState.Indexing;
        }

        return _roots.Values.Any(static root => root.Status == RootStatus.Offline) ? IndexerState.PartiallyOffline : IndexerState.Idle;
    }

    private string IndexingDetails()
    {
        var active = _progress.Values.Where(static progress => progress.Phase != IndexPhase.Done).ToArray();
        if (active.Length == 0)
        {
            return "…";
        }

        var written = active.Sum(static progress => progress.Written);
        return written > 0 ? ": " + written.ToString("N0", CultureInfo.GetCultureInfo("ru-RU")) + L.T("TrayStatus.Entries") : "…";
    }
}

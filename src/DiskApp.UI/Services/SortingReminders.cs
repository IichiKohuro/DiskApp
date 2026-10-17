using System.ComponentModel;

using DiskApp.UI.Localization;
using DiskApp.UI.Text;
using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Services;

/// <summary>
/// Напоминание о разборе (PLAN.md, этап 21.3, вопрос 13): одна заявка «в удобный момент» на все накопители, поводы
/// объединяются — пришли новые файлы (через 2 часа после первого: может, разберёте сами; лежавшие до запуска — не новость),
/// неразобранных набралось 10, вечер (18:00), файлы лежат дольше трёх дней. Ничего не ждёт — заявка снимается; отложенные и «оставленные здесь» не считаются.
/// Показанную заявку двигает только планировщик и кнопки: новые поводы её не торопят.
/// </summary>
public sealed class SortingReminders : IDisposable
{
    /// <summary>Ключ заявки.</summary>
    public const string RequestId = "sorting";

    /// <summary>Вид заявки.</summary>
    public const string Kind = "sorting";

    private readonly ReminderScheduler _scheduler;
    private readonly SortingViewModel _sorting;
    private readonly IAppCommands? _commands;
    private readonly Func<DateTimeOffset> _clock;
    private readonly DateTimeOffset _startedAt;
    private Timer? _timer;
    private DateTime? _shownOn;

    /// <summary>Создаёт источник и регистрирует вид заявки.</summary>
    public SortingReminders(ReminderScheduler scheduler, SortingViewModel sorting, IAppCommands? commands = null, Func<DateTimeOffset>? clock = null)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _sorting = sorting ?? throw new ArgumentNullException(nameof(sorting));
        _commands = commands;
        _clock = clock ?? (static () => DateTimeOffset.Now);
        _startedAt = _clock();
        _scheduler.Register(Kind, Build);
        _sorting.PropertyChanged += OnSortingChanged;
    }

    /// <summary>Неразобранных столько — напомнить, не дожидаясь вечера.</summary>
    public int Threshold { get; init; } = 10;

    /// <summary>Пришли новые файлы — напомнить не раньше чем через столько.</summary>
    public TimeSpan NewFilesDelay { get; init; } = TimeSpan.FromHours(2);

    /// <summary>Файл лежит дольше — «давно не разбирали».</summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromDays(3);

    /// <summary>Ежедневный повод.</summary>
    public TimeOnly EveningAt { get; init; } = new(18, 0);

    /// <summary>Пересматривает поводы раз в <paramref name="interval"/>: вечер наступает и без новых файлов.</summary>
    public void Start(IDispatcher dispatcher, TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        var period = interval ?? TimeSpan.FromMinutes(15);
        _timer ??= new Timer(_ => dispatcher.Post(Evaluate), null, period, period);
    }

    /// <summary>Пересматривает поводы и ставит, торопит или снимает заявку.</summary>
    public void Evaluate()
    {
        var waiting = _sorting.WaitingItems;
        var now = _clock();
        if (waiting.Count == 0)
        {
            _scheduler.Complete(RequestId);
            return;
        }

        var today = now.LocalDateTime.Date;
        var evening = new DateTimeOffset(DateTime.SpecifyKind(today.Add(EveningAt.ToTimeSpan()), DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(today));
        var candidates = new List<DateTimeOffset>();

        // Пришли за сеанс: время файла позже запуска (у загрузки — время окончания).
        var arrived = waiting.Where(item => item.Entry.Modified > _startedAt).Select(static item => item.Entry.Modified).ToList();
        if (arrived.Count > 0)
        {
            candidates.Add(arrived.Min() + NewFilesDelay);
        }

        if (waiting.Count >= Threshold || waiting.Min(static item => item.Entry.Modified) <= now - StaleAfter)
        {
            candidates.Add(now);
        }

        if (_shownOn != today)
        {
            candidates.Add(evening > now ? evening : now);
        }

        if (candidates.Count == 0)
        {
            return;
        }

        var due = candidates.Min();
        var existing = _scheduler.Pending.FirstOrDefault(static request => request.Id == RequestId);
        if (existing is null)
        {
            _scheduler.Schedule(new ReminderRequest(RequestId, Kind, due));
        }
        else if (existing.Snoozes == 0 && due < existing.Due)
        {
            _scheduler.Schedule(existing with { Due = due });
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        _sorting.PropertyChanged -= OnSortingChanged;
    }

    private void OnSortingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SortingViewModel.BadgeValue))
        {
            Evaluate();
        }
    }

    // Уведомление собирается в момент показа: числа — свежие.
    private Notification? Build(ReminderRequest request)
    {
        var waiting = _sorting.WaitingItems;
        if (waiting.Count == 0)
        {
            return null;
        }

        var now = _clock();
        _shownOn = now.LocalDateTime.Date;
        var count = waiting.Count;
        var fresh = waiting.Count(item => item.Entry.Modified.LocalDateTime.Date == _shownOn);
        var number = Formatting.Number(count);
        var text = fresh == count
            ? L.F(Formatting.Plural(count, "SortingReminder.NewSinceMorning.One", "SortingReminder.NewSinceMorning.Few", "SortingReminder.NewSinceMorning.Many"), number)
            : L.F(Formatting.Plural(count, "SortingReminder.Waiting.One", "SortingReminder.Waiting.Few", "SortingReminder.Waiting.Many"), number)
                + (fresh > 0 ? L.F("SortingReminder.NewToday", Formatting.Number(fresh)) : string.Empty);
        List<NotificationAction> actions = [];
        if (_commands is { } commands)
        {
            actions.Add(new NotificationAction(L.T("SortingReminder.Sort"), () =>
            {
                _scheduler.Complete(RequestId);
                commands.NavigateTo(PageKeys.Inbox);
            }));
        }

        actions.Add(ReminderScheduler.LaterAction());
        actions.Add(_scheduler.NotTodayAction(request.Id));
        return new Notification(NotificationKind.Sorting, L.T("SortingReminder.Title"), text) { Actions = actions };
    }
}

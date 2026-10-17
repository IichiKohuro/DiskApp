using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DiskApp.Contracts.App;
using DiskApp.UI.Localization;

namespace DiskApp.UI.ViewModels;

/// <summary>
/// «Уведомления» (PLAN.md, этап 21.5, ADR-0013, п. 5): какие виды показывать, настойчивость, часы тишины, «Не
/// беспокоить» Windows и свой «не беспокоить» на час или до завтра. Каждое изменение сразу сохраняется; канал и
/// планировщик получают его через событие настроек.
/// </summary>
public sealed partial class SettingsViewModel
{
    private static readonly SettingOption<NotificationPersistence>[] PersistenceOptions =
    [
        new(NotificationPersistence.Rare, "Settings.NotificationsRare"),
        new(NotificationPersistence.Normal, "Settings.NotificationsNormal"),
        new(NotificationPersistence.Often, "Settings.NotificationsOften"),
    ];

    /// <summary>Часы для «на час» и «до завтра» (тесты подменяют).</summary>
    public Func<DateTimeOffset> Clock { get; init; } = static () => DateTimeOffset.Now;

    /// <summary>Варианты настойчивости.</summary>
    public IReadOnlyList<SettingOption<NotificationPersistence>> Persistences { get; } = PersistenceOptions;

    /// <summary>Сводка авторазбора.</summary>
    [ObservableProperty]
    public partial bool NotifyAutoSort { get; set; } = true;

    /// <summary>Итог долгих и неудачных операций.</summary>
    [ObservableProperty]
    public partial bool NotifyOperations { get; set; } = true;

    /// <summary>Недоступные папки и ошибки индекса.</summary>
    [ObservableProperty]
    public partial bool NotifyIndex { get; set; } = true;

    /// <summary>Напоминание о разборе накопителей.</summary>
    [ObservableProperty]
    public partial bool NotifySortingReminders { get; set; } = true;

    /// <summary>Настойчивость.</summary>
    [ObservableProperty]
    public partial SettingOption<NotificationPersistence> SelectedPersistence { get; set; } = PersistenceOptions[1];

    /// <summary>Часы тишины включены.</summary>
    [ObservableProperty]
    public partial bool QuietHoursEnabled { get; set; }

    /// <summary>Начало тишины.</summary>
    [ObservableProperty]
    public partial TimeSpan? QuietFrom { get; set; } = new TimeSpan(22, 0, 0);

    /// <summary>Конец тишины.</summary>
    [ObservableProperty]
    public partial TimeSpan? QuietTo { get; set; } = new TimeSpan(8, 0, 0);

    /// <summary>Учитывать «Не беспокоить» Windows.</summary>
    [ObservableProperty]
    public partial bool RespectWindowsDoNotDisturb { get; set; } = true;

    /// <summary>«Не беспокоить» в DiskApp до этого времени.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDoNotDisturbOn), nameof(DoNotDisturbText))]
    public partial DateTimeOffset? DoNotDisturbUntil { get; set; }

    /// <summary>«Не беспокоить» сейчас включено.</summary>
    public bool IsDoNotDisturbOn => DoNotDisturbUntil is { } until && until > Clock();

    /// <summary>«Включено до 15:30» или «Выключено».</summary>
    public string DoNotDisturbText => DoNotDisturbUntil is { } until && until > Clock()
        ? L.F("Settings.DoNotDisturbUntil", until.LocalDateTime.Date == Clock().LocalDateTime.Date
            ? until.LocalDateTime.ToString("t", CultureInfo.CurrentCulture)
            : until.LocalDateTime.ToString("g", CultureInfo.CurrentCulture))
        : L.T("Settings.DoNotDisturbOff");

    /// <summary>«Не беспокоить» на час.</summary>
    [RelayCommand]
    private void DoNotDisturbForHour() => DoNotDisturbUntil = Clock().AddHours(1);

    /// <summary>«Не беспокоить» до завтрашнего утра (9:00).</summary>
    [RelayCommand]
    private void DoNotDisturbUntilTomorrow()
    {
        var morning = Clock().LocalDateTime.Date.AddDays(1).AddHours(9);
        DoNotDisturbUntil = new DateTimeOffset(morning, TimeZoneInfo.Local.GetUtcOffset(morning));
    }

    /// <summary>Выключить «не беспокоить».</summary>
    [RelayCommand]
    private void DoNotDisturbOff() => DoNotDisturbUntil = null;

    private void ApplyNotifications(NotificationSettingsDto settings)
    {
        NotifyAutoSort = settings.AutoSort;
        NotifyOperations = settings.Operations;
        NotifyIndex = settings.Index;
        NotifySortingReminders = settings.SortingReminders;
        SelectedPersistence = PersistenceOptions.FirstOrDefault(option => option.Value == settings.Persistence) ?? PersistenceOptions[1];
        QuietHoursEnabled = settings.QuietHours;
        QuietFrom = ParseTime(settings.QuietFrom) ?? new TimeSpan(22, 0, 0);
        QuietTo = ParseTime(settings.QuietTo) ?? new TimeSpan(8, 0, 0);
        RespectWindowsDoNotDisturb = settings.RespectWindowsDoNotDisturb;
        DoNotDisturbUntil = settings.DoNotDisturbUntil;
    }

    private static TimeSpan? ParseTime(string? text)
        => TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time.ToTimeSpan() : null;

    private static string FormatTime(TimeSpan time) => TimeOnly.FromTimeSpan(time).ToString("HH:mm", CultureInfo.InvariantCulture);

    partial void OnNotifyAutoSortChanged(bool value) => Save("notifications", "autoSort", value);

    partial void OnNotifyOperationsChanged(bool value) => Save("notifications", "operations", value);

    partial void OnNotifyIndexChanged(bool value) => Save("notifications", "index", value);

    partial void OnNotifySortingRemindersChanged(bool value) => Save("notifications", "sortingReminders", value);

    partial void OnSelectedPersistenceChanged(SettingOption<NotificationPersistence> value) => Save("notifications", "persistence", value.Value.ToString());

    partial void OnQuietHoursEnabledChanged(bool value) => Save("notifications", "quietHours", value);

    partial void OnQuietFromChanged(TimeSpan? value)
    {
        if (value is { } time)
        {
            Save("notifications", "quietFrom", FormatTime(time));
        }
    }

    partial void OnQuietToChanged(TimeSpan? value)
    {
        if (value is { } time)
        {
            Save("notifications", "quietTo", FormatTime(time));
        }
    }

    partial void OnRespectWindowsDoNotDisturbChanged(bool value) => Save("notifications", "respectWindowsDoNotDisturb", value);

    partial void OnDoNotDisturbUntilChanged(DateTimeOffset? value)
        => Save("notifications", "doNotDisturbUntil", value is { } until ? until.ToString("O", CultureInfo.InvariantCulture) : null);
}

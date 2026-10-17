using DiskApp.Client;
using DiskApp.Contracts.App;

namespace DiskApp.UI.Services;

/// <summary>
/// Настройки «Уведомлений» (PLAN.md, этап 21.5) — в канал и планировщик: при запуске сервера и при каждом изменении
/// настроек (событие хаба), откуда бы оно ни пришло.
/// </summary>
public sealed class NotificationSettingsApplier(INotificationService notifications, ReminderScheduler scheduler)
{
    private readonly INotificationService _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
    private readonly ReminderScheduler _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));

    /// <summary>Слушает изменения настроек; они применяются на потоке интерфейса.</summary>
    public void Attach(DiskAppHubConnection hub, IDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(dispatcher);
        hub.SettingsChanged += settings => dispatcher.Post(() => Apply(settings));
    }

    /// <summary>Читает настройки и применяет; без сервера остаются значения по умолчанию.</summary>
    public async Task LoadAsync(ISettingsService settings, IDispatcher dispatcher, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dispatcher);
        try
        {
            var current = await settings.GetAsync(cancellationToken).ConfigureAwait(false);
            dispatcher.Post(() => Apply(current));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Сервер не ответил: лимиты по умолчанию, следующее изменение настроек придёт через хаб.
            _ = exception;
        }
    }

    /// <summary>Применяет настройки.</summary>
    public void Apply(SettingsDto settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var preferences = NotificationPreferences.From(settings.Notifications);
        _notifications.Apply(preferences);
        _scheduler.Apply(preferences);
    }
}

using Avalonia.Threading;

using DiskApp.Desktop.Windows;
using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

namespace DiskApp.Desktop.Shell;

/// <summary>
/// Оболочка уведомлений десктопа (PLAN.md, этап 21.1): открыто ли главное окно — спрашивает у оболочки, карточку
/// показывает отдельным окном у области уведомлений. Карточка одна: новая заменяет прежнюю.
/// </summary>
internal sealed class NotificationCardPresenter(Func<bool> isMainWindowVisible) : INotificationHost
{
    private NotificationCardWindow? _current;

    /// <inheritdoc />
    public bool IsMainWindowVisible => isMainWindowVisible();

    /// <inheritdoc />
    public void ShowCard(NotificationCardViewModel card)
    {
        ArgumentNullException.ThrowIfNull(card);
        Dispatcher.UIThread.Post(() =>
        {
            _current?.Close();
            var window = new NotificationCardWindow(card);
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_current, window))
                {
                    _current = null;
                }
            };
            _current = window;
            window.Show();
        });
    }
}

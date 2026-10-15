using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

using DiskApp.UI.ViewModels;

namespace DiskApp.Desktop.Windows;

/// <summary>
/// Окно тихой карточки уведомления (PLAN.md, этап 21.1, ADR-0013): встаёт в углу рабочей области у области уведомлений
/// основного монитора, не забирает фокус, закрывается кнопкой, действием или само через
/// <see cref="NotificationCardViewModel.AutoCloseAfter"/>; пока над ним указатель — не закрывается.
/// </summary>
public partial class NotificationCardWindow : Window
{
    private const double EdgeMargin = 12;
    private readonly DispatcherTimer _autoClose = new();

    /// <summary>Конструктор для дизайнера.</summary>
    public NotificationCardWindow()
    {
        InitializeComponent();
    }

    /// <summary>Создаёт окно карточки.</summary>
    public NotificationCardWindow(NotificationCardViewModel card)
        : this()
    {
        ArgumentNullException.ThrowIfNull(card);
        DataContext = card;
        card.CloseRequested += (_, _) => Close();
        _autoClose.Interval = card.AutoCloseAfter;
        _autoClose.Tick += (_, _) => Close();
        PointerEntered += (_, _) => _autoClose.Stop();
        PointerExited += (_, _) => _autoClose.Start();

        // Высота известна только после разметки: окно появляется за экраном и встаёт на место, когда она посчитана.
        Position = new PixelPoint(-32000, -32000);
        Opened += (_, _) => Dispatcher.UIThread.Post(Place, DispatcherPriority.Loaded);
        Closed += (_, _) => _autoClose.Stop();
    }

    private void Place()
    {
        if (Screens.Primary is not { } screen)
        {
            return;
        }

        var size = new PixelSize((int)Math.Round(Bounds.Width * screen.Scaling), (int)Math.Round(Bounds.Height * screen.Scaling));

        // «Курсор» в середине монитора, не на панели задач: карточка встаёт в углу у области уведомлений, на какой бы
        // стороне ни была панель.
        var center = new PixelPoint(screen.Bounds.X + (screen.Bounds.Width / 2), screen.Bounds.Y + (screen.Bounds.Height / 2));
        Position = DropWindowPlacement.Compute(screen.Bounds, screen.WorkingArea, center, size, (int)Math.Round(EdgeMargin * screen.Scaling));
        _autoClose.Start();
    }
}

using System.Diagnostics;

using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

using DiskApp.Desktop.Platform.Windows;
using DiskApp.UI.Models;
using DiskApp.UI.ViewModels;

using Serilog;

namespace DiskApp.Desktop.Windows;

/// <summary>
/// Окно быстрого поиска. Создаётся заранее и прячется, чтобы показ по хоткею был мгновенным;
/// появляется в верхней трети монитора с курсором.
/// </summary>
public partial class QuickSearchWindow : Window
{
    private QuickSearchViewModel? _viewModel;
    private int _showCount;

    /// <summary>
    /// Создаёт окно и подключает скрытие при потере фокуса.
    /// </summary>
    public QuickSearchWindow()
    {
        InitializeComponent();
        Deactivated += OnDeactivated;
    }

    /// <summary>
    /// Разрешить настоящее закрытие. Пока <c>false</c>, закрытие лишь прячет окно.
    /// </summary>
    public bool AllowClose { get; set; }

    /// <summary>
    /// Без анимации появления: настройка «уменьшить движение» в приложении или выключенные эффекты анимации Windows.
    /// </summary>
    public bool ReduceMotion { get; set; }

    /// <summary>
    /// Показывает окно на мониторе с точкой <paramref name="anchor"/> (обычно курсор) или на основном,
    /// активирует его и ставит фокус в поле запроса.
    /// </summary>
    public void ShowAt(PixelPoint? anchor)
    {
        var watch = Stopwatch.StartNew();
        var showNumber = ++_showCount;

        var screens = Screens;
        var screen = (anchor is { } point ? screens.ScreenFromPoint(point) : null)
            ?? screens.Primary
            ?? (screens.All.Count > 0 ? screens.All[0] : null);
        if (screen is not null)
        {
            var area = screen.WorkingArea;
            var width = (int)Math.Round(Width * screen.Scaling);
            var height = (int)Math.Round(Height * screen.Scaling);
            Position = new PixelPoint(area.X + ((area.Width - width) / 2), area.Y + ((area.Height - height) / 3));
        }

        if (!IsVisible)
        {
            PrepareShowAnimation();
            Show();
            PlayShowAnimation();
        }

        Activate();
        SearchView.FocusQuery();

        // Замер S1: от вызова до первого кадра. Первый показ включает построение шаблонов, последующие — только показ.
        RequestAnimationFrame(_ => Log.Debug("Окно поиска: показ №{Number} за {Milliseconds:F0} мс до первого кадра", showNumber, watch.Elapsed.TotalMilliseconds));
    }

    /// <summary>
    /// Хоткей: активное окно прячется, иначе показывается.
    /// </summary>
    public void Toggle(PixelPoint? anchor)
    {
        if (IsVisible && IsActive)
        {
            Hide();
        }
        else
        {
            ShowAt(anchor);
        }
    }

    /// <inheritdoc />
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (OperatingSystem.IsWindows())
        {
            WindowEffects.TryApplyRoundedCorners(this);
        }

        UpdateBackdrop();
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.CloseRequested -= OnCloseRequested;
            _viewModel.FileOpened -= OnFileOpened;
            _viewModel.DismissRequested -= OnCloseRequested;
        }

        _viewModel = DataContext as QuickSearchViewModel;

        if (_viewModel is not null)
        {
            _viewModel.CloseRequested += OnCloseRequested;
            _viewModel.FileOpened += OnFileOpened;
            _viewModel.DismissRequested += OnCloseRequested;
        }
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ActualTransparencyLevelProperty)
        {
            UpdateBackdrop();
        }
    }

    /// <inheritdoc />
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (!AllowClose && !e.Cancel)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private static readonly TimeSpan ShowDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TransformOperations Lifted = TransformOperations.Parse("translateY(-8px)");
    private static readonly TransformOperations Resting = TransformOperations.Parse("translateY(0px)");

    /// <summary>
    /// Появление (PLAN.md, этап 6.8): содержимое проявляется и опускается на 8 px за 150 мс. Анимируется содержимое, а не окно:
    /// прозрачность всего окна у Avalonia на Windows не анимируется. При «уменьшить движение» окно появляется сразу.
    /// </summary>
    private void PrepareShowAnimation()
    {
        Root.Transitions = null;
        if (ReduceMotion)
        {
            Root.Opacity = 1;
            Root.RenderTransform = null;
            return;
        }

        Root.Opacity = 0;
        Root.RenderTransform = Lifted;
    }

    private void PlayShowAnimation()
    {
        if (ReduceMotion)
        {
            return;
        }

        Root.Transitions =
        [
            new DoubleTransition { Property = OpacityProperty, Duration = ShowDuration, Easing = new CubicEaseOut() },
            new TransformOperationsTransition { Property = RenderTransformProperty, Duration = ShowDuration, Easing = new CubicEaseOut() },
        ];
        Dispatcher.UIThread.Post(() =>
        {
            Root.Opacity = 1;
            Root.RenderTransform = Resting;
        }, DispatcherPriority.Render);
    }

    private void UpdateBackdrop()
    {
        // Уровень прозрачности меняется ещё при загрузке разметки, до присвоения именованных полей.
        if (Acrylic is null)
        {
            return;
        }

        var level = ActualTransparencyLevel;
        Acrylic.IsVisible = level.Equals(WindowTransparencyLevel.AcrylicBlur) || level.Equals(WindowTransparencyLevel.Blur);
    }

    private void OnDeactivated(object? sender, EventArgs e) => Hide();

    private void OnCloseRequested(object? sender, EventArgs e) => Hide();

    private void OnFileOpened(object? sender, FileEntry entry) => Hide();
}

using System.Diagnostics;

using Avalonia;
using Avalonia.Controls;
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

    private const double BaseWidth = 720;
    private const double BaseHeight = 500;

    /// <summary>Прятать окно при потере фокуса (настройка окна поиска, этап 9.1); по умолчанию — да.</summary>
    public bool HideOnDeactivate { get; set; } = true;

    /// <summary>
    /// Внешний вид из настроек (PLAN.md, этап 9.1): Acrylic или сплошной фон, масштаб окна и содержимого, компактные списки.
    /// </summary>
    public void ApplyAppearance(DiskApp.Core.Settings.BackdropMode backdrop, double scale, bool compact)
    {
        TransparencyLevelHint = PopupChrome.TransparencyFor(backdrop);
        var unit = Math.Abs(scale - 1.0) < 0.01;
        Scaler.LayoutTransform = unit ? null : new Avalonia.Media.ScaleTransform(scale, scale);
        Width = Math.Round(BaseWidth * scale);
        Height = Math.Round(BaseHeight * scale);
        Root.Classes.Set("compact", compact);
    }

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
        Dispatcher.UIThread.Post(() => _viewModel?.ShowTips(), DispatcherPriority.Background);

        // Замер S1: от вызова до первого кадра. Первый показ включает построение шаблонов, последующие — только показ.
        RequestAnimationFrame(_ => Log.Information("Окно поиска: показ №{Number} за {Milliseconds:F0} мс до первого кадра", showNumber, watch.Elapsed.TotalMilliseconds));
    }

    /// <summary>
    /// Прогрев перед первым показом (PLAN.md, этап 10.2): окно один раз рисуется за пределами экрана без активации и сразу
    /// прячется. Шаблоны, раскладка и первый кадр готовы заранее, и первый показ по хоткею не платит за них (было 100–180 мс).
    /// Если пользователь вызвал окно, пока идёт прогрев, оно остаётся на экране.
    /// </summary>
    public void Prewarm()
    {
        if (IsVisible || _showCount > 0)
        {
            return;
        }

        ShowActivated = false;
        Position = new PixelPoint(-32000, -32000);
        Show();
        RequestAnimationFrame(_ => Dispatcher.UIThread.Post(
            () =>
            {
                ShowActivated = true;
                if (_showCount == 0)
                {
                    Hide();
                }

                Log.Debug("Окно поиска прогрето");
            },
            DispatcherPriority.Background));
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

        if (change.Property == ActualTransparencyLevelProperty || change.Property == ActualThemeVariantProperty)
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

    /// <summary>
    /// Появление (PLAN.md, этап 6.8): содержимое проявляется и опускается на 8 px за 150 мс. Анимируется содержимое, а не окно:
    /// прозрачность всего окна у Avalonia на Windows не анимируется. При «уменьшить движение» окно появляется сразу.
    /// </summary>
    private void PrepareShowAnimation() => PopupChrome.PrepareShow(Root, ReduceMotion, offset: -8);

    private void PlayShowAnimation() => PopupChrome.PlayShow(Root, ReduceMotion);

    private void UpdateBackdrop() => PopupChrome.UpdateBackdrop(this, Acrylic, Solid);

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (HideOnDeactivate)
        {
            Hide();
        }
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Hide();

    private void OnFileOpened(object? sender, FileEntry entry) => Hide();
}

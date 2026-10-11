using System.Diagnostics;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

using DiskApp.Core.Settings;
using DiskApp.Desktop.Platform.Windows;
using DiskApp.UI.ViewModels;

using Serilog;

namespace DiskApp.Desktop.Windows;

/// <summary>
/// Окно Drag&amp;Drop (PLAN.md, этап 18, ADR-0011): открывается щелчком по значку в трее или своим хоткеем у панели задач
/// монитора с курсором. Не прячется при потере фокуса — за файлами пользователь переключается в другое окно; прячется по
/// <c>Esc</c>, кнопке закрытия, повторному щелчку по значку и хоткею, а после удачного броска — само через несколько секунд,
/// если пользователь не наводит на него мышь и не несёт новые файлы. Создаётся заранее и прогревается, как окно поиска.
/// </summary>
public partial class DropWindow : Window
{
    private const double BaseWidth = 360;
    private const double BaseHeight = 270;
    private const double EdgeMargin = 12;
    private static readonly TimeSpan AutoHideDelay = TimeSpan.FromSeconds(4);

    private readonly DispatcherTimer _autoHide = new() { Interval = AutoHideDelay };
    private DropWindowViewModel? _viewModel;
    private int _showCount;
    private bool _autoHidePending;

    /// <summary>
    /// Создаёт окно; показывает его оболочка.
    /// </summary>
    public DropWindow()
    {
        InitializeComponent();
        _autoHide.Tick += (_, _) =>
        {
            StopAutoHide();
            Hide();
        };

        // Пока мышь над окном или несут файлы, оно не прячется: пользователь читает итог или бросает ещё.
        PointerEntered += (_, _) => _autoHide.Stop();
        PointerExited += (_, _) => RestartAutoHide();
        View.DragStarted += (_, _) => StopAutoHide();
    }

    /// <summary>Разрешить настоящее закрытие (при выходе из приложения); иначе закрытие прячет окно.</summary>
    public bool AllowClose { get; set; }

    /// <summary>Без анимации появления.</summary>
    public bool ReduceMotion { get; set; }

    /// <summary>
    /// Внешний вид из настроек: Acrylic или сплошной фон, масштаб окна и содержимого, компактность.
    /// </summary>
    public void ApplyAppearance(BackdropMode backdrop, double scale, bool compact)
    {
        TransparencyLevelHint = PopupChrome.TransparencyFor(backdrop);
        Scaler.LayoutTransform = Math.Abs(scale - 1.0) < 0.01 ? null : new Avalonia.Media.ScaleTransform(scale, scale);
        Width = Math.Round(BaseWidth * scale);
        Height = Math.Round(BaseHeight * scale);
        Root.Classes.Set("compact", compact);
    }

    /// <summary>
    /// Показывает окно у панели задач монитора с точкой <paramref name="cursor"/>: напротив курсора, если он на панели
    /// (щелчок по значку), иначе в углу у области уведомлений.
    /// </summary>
    public void ShowNear(PixelPoint cursor)
    {
        var watch = Stopwatch.StartNew();
        var showNumber = ++_showCount;

        var screens = Screens;
        if ((screens.ScreenFromPoint(cursor) ?? screens.Primary ?? (screens.All.Count > 0 ? screens.All[0] : null)) is { } screen)
        {
            var size = new PixelSize((int)Math.Round(Width * screen.Scaling), (int)Math.Round(Height * screen.Scaling));
            Position = DropWindowPlacement.Compute(screen.Bounds, screen.WorkingArea, cursor, size, (int)Math.Round(EdgeMargin * screen.Scaling));
        }

        if (!IsVisible)
        {
            // Окно у нижней панели поднимается снизу, у верхней — опускается сверху.
            PopupChrome.PrepareShow(Root, ReduceMotion, offset: 8);
            Show();
            PopupChrome.PlayShow(Root, ReduceMotion);
        }

        Activate();
        _viewModel?.OnShown();
        RequestAnimationFrame(_ => Log.Information("Окно Drag&Drop: показ №{Number} за {Milliseconds:F0} мс до первого кадра", showNumber, watch.Elapsed.TotalMilliseconds));
    }

    /// <summary>
    /// Щелчок по значку или хоткей: открытое окно прячется, иначе показывается.
    /// </summary>
    public void Toggle(PixelPoint cursor)
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            ShowNear(cursor);
        }
    }

    /// <summary>
    /// Прогрев перед первым показом, как у окна поиска (этап 10.2): окно один раз рисуется за пределами экрана и прячется.
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

                Log.Debug("Окно Drag&Drop прогрето");
            },
            DispatcherPriority.Background));
    }

    /// <inheritdoc />
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (OperatingSystem.IsWindows())
        {
            WindowEffects.TryApplyRoundedCorners(this);
        }

        PopupChrome.UpdateBackdrop(this, Acrylic, Solid);
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.CloseRequested -= OnCloseRequested;
            _viewModel.Settled -= OnSettled;
        }

        _viewModel = DataContext as DropWindowViewModel;

        if (_viewModel is not null)
        {
            _viewModel.CloseRequested += OnCloseRequested;
            _viewModel.Settled += OnSettled;
        }
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ActualTransparencyLevelProperty || change.Property == ActualThemeVariantProperty)
        {
            PopupChrome.UpdateBackdrop(this, Acrylic, Solid);
        }
        else if (change.Property == IsVisibleProperty && change.GetNewValue<bool>() is false)
        {
            StopAutoHide();
            _viewModel?.OnHidden();
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

    private void OnCloseRequested(object? sender, EventArgs e) => Hide();

    // Удачный бросок: окно уйдёт само через несколько секунд, если мышь не над ним.
    private void OnSettled(object? sender, EventArgs e)
    {
        _autoHidePending = true;
        if (!IsPointerOver)
        {
            _autoHide.Stop();
            _autoHide.Start();
        }
    }

    private void RestartAutoHide()
    {
        if (_autoHidePending && IsVisible)
        {
            _autoHide.Stop();
            _autoHide.Start();
        }
    }

    private void StopAutoHide()
    {
        _autoHidePending = false;
        _autoHide.Stop();
    }
}

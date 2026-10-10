using System.Diagnostics;

using Avalonia;
using Avalonia.Controls;

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
            Show();
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
        }

        _viewModel = DataContext as QuickSearchViewModel;

        if (_viewModel is not null)
        {
            _viewModel.CloseRequested += OnCloseRequested;
            _viewModel.FileOpened += OnFileOpened;
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

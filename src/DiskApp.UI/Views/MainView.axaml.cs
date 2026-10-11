using System.ComponentModel;

using Avalonia.Animation;
using Avalonia.Controls;

using DiskApp.UI.ViewModels;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.Views;

/// <summary>
/// Содержимое главного окна: навигация, полоса уведомлений, страницы. Страницы сменяются плавно, а при
/// «уменьшить движение» (<see cref="MainViewModel.ReduceMotion"/>) — сразу.
/// </summary>
public partial class MainView : UserControl
{
    private static readonly IPageTransition Fade = new CrossFade(TimeSpan.FromMilliseconds(200));
    private MainViewModel? _viewModel;

    /// <summary>
    /// Создаёт представление.
    /// </summary>
    public MainView()
    {
        InitializeComponent();
    }

    /// <summary>Панель навигации — для окна, которому нужен доступ к ней.</summary>
    public FANavigationView NavigationView => Navigation;

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        ApplyMotion();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ReduceMotion))
        {
            ApplyMotion();
        }
    }

    private void ApplyMotion() => PageHost.PageTransition = _viewModel?.ReduceMotion == true ? null : Fade;
}

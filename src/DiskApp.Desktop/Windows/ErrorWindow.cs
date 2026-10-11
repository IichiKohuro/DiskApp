using Avalonia.Controls;
using Avalonia.Platform;

using DiskApp.UI.ViewModels;
using DiskApp.UI.Views;

namespace DiskApp.Desktop.Windows;

/// <summary>
/// Окно «Что-то пошло не так» (PLAN.md, этап 9.5): обычное окно поверх остальных, не зависит от главного окна, поэтому
/// открывается, даже когда ошибка случилась в нём или оно скрыто в трее.
/// </summary>
internal sealed class ErrorWindow : Window
{
    /// <summary>Создаёт окно для модели.</summary>
    public ErrorWindow(ErrorReportViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        Title = viewModel.Title;
        Width = 600;
        MaxHeight = 720;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://DiskApp/Assets/DiskApp.ico")));
        DataContext = viewModel;
        Content = new ErrorReportView { DataContext = viewModel };
        viewModel.CloseRequested += (_, _) => Close();
    }
}

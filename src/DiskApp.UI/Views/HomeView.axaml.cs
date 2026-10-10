using Avalonia.Controls;
using Avalonia.Input;

using DiskApp.UI.Models;
using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Views;

/// <summary>
/// Главная страница: приветствие, состояние индекса, быстрые действия и недавние файлы.
/// </summary>
public partial class HomeView : UserControl
{
    /// <summary>
    /// Создаёт страницу и подключает открытие недавнего файла двойным щелчком.
    /// </summary>
    public HomeView()
    {
        InitializeComponent();
        RecentList.DoubleTapped += OnRecentDoubleTapped;
    }

    private void OnRecentDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is HomeViewModel viewModel && RecentList.SelectedItem is SearchHit hit)
        {
            viewModel.OpenFileCommand.Execute(hit);
        }
    }
}

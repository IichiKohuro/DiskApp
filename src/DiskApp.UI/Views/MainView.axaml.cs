using Avalonia.Controls;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.Views;

/// <summary>
/// Содержимое главного окна, общее для всех платформ: панель навигации и страницы.
/// </summary>
public partial class MainView : UserControl
{
    /// <summary>
    /// Создаёт представление и загружает его разметку.
    /// </summary>
    public MainView()
    {
        InitializeComponent();
    }

    /// <summary>Панель навигации; нужна тестам и оболочке.</summary>
    public FANavigationView NavigationView => Navigation;
}

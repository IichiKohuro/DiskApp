using Avalonia.Controls;

namespace DiskApp.UI.Views;

/// <summary>
/// Содержимое главного окна, общее для всех платформ.
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
}

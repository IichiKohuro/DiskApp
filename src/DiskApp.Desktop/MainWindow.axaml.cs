using Avalonia.Controls;

namespace DiskApp.Desktop;

/// <summary>
/// Главное окно десктопа. Стиль Windows 11 (Mica, собственный заголовок) добавляют спайк S1 и этап 6.7.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// Создаёт окно и загружает его разметку.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
    }
}

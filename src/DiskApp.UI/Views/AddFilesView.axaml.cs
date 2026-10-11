using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DiskApp.UI.Views;

/// <summary>
/// Содержимое окна «Добавить»: фокус сразу в поиске папки.
/// </summary>
public partial class AddFilesView : UserControl
{
    /// <summary>
    /// Создаёт представление.
    /// </summary>
    public AddFilesView()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        QueryBox.Focus();
    }
}

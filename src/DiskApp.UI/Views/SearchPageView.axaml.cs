using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DiskApp.UI.Views;

/// <summary>
/// Страница «Поиск» главного окна.
/// </summary>
public partial class SearchPageView : UserControl
{
    /// <summary>
    /// Создаёт страницу; при показе фокус уходит в поле запроса.
    /// </summary>
    public SearchPageView()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Search.FocusQuery();
    }
}

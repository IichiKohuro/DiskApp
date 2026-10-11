using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

using DiskApp.UI.Models;
using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Views;

/// <summary>
/// Содержимое быстрого поиска, общее для всплывающего окна и страницы «Поиск».
/// Клавиатура: <c>↑</c>/<c>↓</c>, <c>PgUp</c>/<c>PgDn</c>, <c>Enter</c>, <c>Esc</c>, <c>Ctrl+1…9</c>.
/// </summary>
public partial class QuickSearchView : UserControl
{
    private const int PageStep = 10;

    /// <summary>
    /// Создаёт представление и подключает обработку клавиш и двойного щелчка.
    /// </summary>
    public QuickSearchView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        ResultsList.DoubleTapped += OnResultDoubleTapped;
        ResultsList.ContainerPrepared += OnContainerPrepared;
    }

    private QuickSearchViewModel? ViewModel => DataContext as QuickSearchViewModel;

    /// <summary>
    /// Ставит фокус в поле ввода и выделяет текст, чтобы новый запрос заменил старый.
    /// </summary>
    public void FocusQuery()
    {
        QueryBox.Focus();
        QueryBox.SelectAll();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var handled = true;
        switch (e.Key)
        {
            case Key.Down:
                viewModel.MoveSelection(1);
                break;
            case Key.Up:
                viewModel.MoveSelection(-1);
                break;
            case Key.PageDown:
                viewModel.MoveSelection(PageStep);
                break;
            case Key.PageUp:
                viewModel.MoveSelection(-PageStep);
                break;
            case Key.Enter:
                _ = viewModel.OpenSelectedAsync();
                break;
            case Key.Escape:
                viewModel.Escape();
                break;
            case >= Key.D1 and <= Key.D9 when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                if (viewModel.SelectNumber(e.Key - Key.D0))
                {
                    _ = viewModel.OpenSelectedAsync();
                }

                break;
            default:
                handled = false;
                break;
        }

        if (handled)
        {
            e.Handled = true;
            ScrollToSelection(viewModel);
        }
    }

    // Первая строка — к началу списка, чтобы над ней был виден заголовок секции.
    private void ScrollToSelection(QuickSearchViewModel viewModel)
    {
        if (viewModel.SelectedIndex == 0)
        {
            ResultsList.ScrollIntoView(0);
        }
        else if (viewModel.SelectedRow is { } row)
        {
            ResultsList.ScrollIntoView(row);
        }
    }

    // Контейнеры переиспользуются при прокрутке: класс заголовка ставится и снимается каждый раз.
    private static void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
        => e.Container.Classes.Set("header", e.Container.DataContext is ResultSectionHeader);

    private void OnResultDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            _ = viewModel.OpenSelectedAsync();
        }
    }
}

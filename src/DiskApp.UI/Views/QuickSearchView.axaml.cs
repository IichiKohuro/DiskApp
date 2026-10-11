using System.ComponentModel;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

using DiskApp.UI.Input;
using DiskApp.UI.Models;
using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Views;

/// <summary>
/// Содержимое быстрого поиска, общее для всплывающего окна и страницы «Поиск».
/// Клавиши разбирает <see cref="QuickSearchKeyMap"/>; меню строки — по правой кнопке, <c>Tab</c> или <c>Shift+F10</c>.
/// </summary>
public partial class QuickSearchView : UserControl
{
    private QuickSearchViewModel? _subscribed;

    /// <summary>
    /// Создаёт представление и подключает обработку клавиш, двойного щелчка и контекстного меню.
    /// </summary>
    public QuickSearchView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        ResultsList.DoubleTapped += OnResultDoubleTapped;
        ResultsList.ContainerPrepared += OnContainerPrepared;
        if (ResultsList.ContextFlyout is { } menu)
        {
            menu.Closed += (_, _) => FocusQueryKeepingText();
        }
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

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_subscribed is not null)
        {
            _subscribed.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribed.QueryReplaced -= OnQueryReplaced;
        }

        _subscribed = ViewModel;
        if (_subscribed is not null)
        {
            _subscribed.PropertyChanged += OnViewModelPropertyChanged;
            _subscribed.QueryReplaced += OnQueryReplaced;
        }
    }

    // Чип, подсказка или история заменили текст: курсор — в конец, чтобы продолжить ввод.
    private void OnQueryReplaced(object? sender, EventArgs e) => Dispatcher.UIThread.Post(FocusQueryKeepingText);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(QuickSearchViewModel.PendingAction) || ViewModel is not { } viewModel)
        {
            return;
        }

        // Поле имени появляется после применения привязок: фокус и выделение имени без расширения — следующим кадром.
        Dispatcher.UIThread.Post(() =>
        {
            if (viewModel.IsRenaming)
            {
                RenameBox.Focus();
                RenameBox.SelectionStart = 0;
                RenameBox.SelectionEnd = viewModel.RenameSelectionLength;
            }
            else
            {
                FocusQueryKeepingText();
            }
        });
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        if (viewModel.HasPendingAction)
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
            {
                e.Handled = true;
                _ = viewModel.ConfirmPendingActionAsync();
                return;
            }

            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                viewModel.CancelPendingAction();
                return;
            }

            if (viewModel.IsRenaming)
            {
                // Остальные клавиши — полю нового имени.
                return;
            }

            // Подтверждение корзины: любая другая клавиша закрывает панель и работает как обычно.
            viewModel.CancelPendingAction();
        }

        var text = QueryBox.Text ?? string.Empty;
        var box = new QueryBoxState(
            IsEmpty: text.Length == 0,
            HasSelection: QueryBox.SelectionStart != QueryBox.SelectionEnd,
            CaretAtEnd: QueryBox.CaretIndex >= text.Length,
            HasSuggestions: text.Length > 0 && viewModel.ShowSuggestions);
        if (QuickSearchKeyMap.Resolve(e.Key, e.KeyModifiers, box) is not { } key)
        {
            return;
        }

        e.Handled = true;
        if (key.Command == QuickSearchCommand.ContextMenu)
        {
            ShowContextMenu(viewModel);
            return;
        }

        _ = ExecuteAsync(viewModel, key);
    }

    private async Task ExecuteAsync(QuickSearchViewModel viewModel, QuickSearchKey key)
    {
        await viewModel.ExecuteAsync(key.Command, key.Number);
        ScrollToSelection(viewModel);
    }

    private void ShowContextMenu(QuickSearchViewModel viewModel)
    {
        if (viewModel.SelectedRow is not { } row || ResultsList.ContextFlyout is not { } menu)
        {
            return;
        }

        ResultsList.ScrollIntoView(row);
        var target = ResultsList.ContainerFromItem(row) ?? (Control)ResultsList;
        menu.ShowAt(target);
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

    private void FocusQueryKeepingText()
    {
        QueryBox.Focus();
        QueryBox.CaretIndex = QueryBox.Text?.Length ?? 0;
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

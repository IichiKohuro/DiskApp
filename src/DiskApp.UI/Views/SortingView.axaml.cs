using System.ComponentModel;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Views;

/// <summary>
/// Страница «Разбор»: выбор нескольких файлов в списке, двойной щелчок — открыть файл, <c>1…9</c> — рабочая папка,
/// <c>Enter</c> в списке или <c>Ctrl+Enter</c> где угодно — перенести, <c>Enter</c> в поле тега — добавить тег, в поле
/// шаблона имени — закрыть панель, в поле поиска — первая найденная папка, в поле имени — создать подпапку, <c>Esc</c> —
/// отменить ввод имени. Ширина панели рабочих папок — разделителем, её помнит ViewModel.
/// </summary>
public partial class SortingView : UserControl
{
    private SortingViewModel? _subscribed;

    /// <summary>
    /// Создаёт представление.
    /// </summary>
    public SortingView()
    {
        InitializeComponent();
        ItemsList.SelectionChanged += (_, _) => ViewModel?.SetSelection([.. ItemsList.SelectedItems?.OfType<SortItem>() ?? []]);
        ItemsList.DoubleTapped += (_, e) =>
        {
            // Посмотреть файл, прежде чем решить, куда его положить.
            if ((e.Source as Control)?.DataContext is SortItem item)
            {
                ViewModel?.OpenItemCommand.Execute(item);
                e.Handled = true;
            }
        };
        FolderResultsList.SelectionChanged += (_, _) =>
        {
            if (FolderResultsList.SelectedItem is SortingFolderInfo folder)
            {
                ViewModel?.ChooseFoundFolderCommand.Execute(folder);
            }
        };
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);

        // Поля имени и тегов — во всплывающих панелях: клавиши оттуда до страницы не доходят, ловятся на самих полях.
        TagBox.AddHandler(KeyDownEvent, OnPopupBoxKeyDown, RoutingStrategies.Tunnel);
        NameBox.AddHandler(KeyDownEvent, OnPopupBoxKeyDown, RoutingStrategies.Tunnel);
        ((Flyout)NameButton.Flyout!).Opened += (_, _) => Dispatcher.UIThread.Post(() => NameBox.Focus(), DispatcherPriority.Input);
        ((Flyout)TagsButton.Flyout!).Opened += (_, _) => Dispatcher.UIThread.Post(() => TagBox.Focus(), DispatcherPriority.Input);

        // Готовый вариант только подставляет шаблон: список возвращается к «Готовые варианты», и тот же вариант можно
        // выбрать снова. Сброс — следующим кадром: внутри смены выбора ComboBox его не примет.
        PresetsBox.SelectionChanged += (_, _) =>
        {
            if (PresetsBox.SelectedItem is not null)
            {
                Dispatcher.UIThread.Post(() => PresetsBox.SelectedItem = null, DispatcherPriority.Background);
            }
        };
        PanelsSplitter.DragCompleted += (_, _) =>
        {
            if (ViewModel is { } viewModel)
            {
                viewModel.DestinationColumnWidth = RootGrid.ColumnDefinitions[2].Width;
            }
        };
        DataContextChanged += (_, _) => Subscribe();
    }

    private SortingViewModel? ViewModel => DataContext as SortingViewModel;

    private void Subscribe()
    {
        if (_subscribed is not null)
        {
            _subscribed.PropertyChanged -= OnViewModelChanged;
        }

        _subscribed = ViewModel;
        if (_subscribed is not null)
        {
            _subscribed.PropertyChanged += OnViewModelChanged;
            RootGrid.ColumnDefinitions[2].Width = _subscribed.DestinationColumnWidth;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SortingViewModel.IsNamingFolder) && _subscribed is { IsNamingFolder: true })
        {
            Dispatcher.UIThread.Post(() => NewFolderBox.Focus(), DispatcherPriority.Input);
        }
        else if (e.PropertyName == nameof(SortingViewModel.DestinationColumnWidth) && _subscribed is { } viewModel
            && RootGrid.ColumnDefinitions[2].Width != viewModel.DestinationColumnWidth)
        {
            RootGrid.ColumnDefinitions[2].Width = viewModel.DestinationColumnWidth;
        }
    }

    // Enter в поле тега добавляет тег, в поле шаблона имени — закрывает панель: имя уже применено.
    private void OnPopupBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel || e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        if (ReferenceEquals(sender, TagBox) && viewModel.TagText.Trim().Length > 0)
        {
            viewModel.AddTagCommand.Execute(null);
            e.Handled = true;
        }
        else if (ReferenceEquals(sender, NameBox))
        {
            NameButton.Flyout?.Hide();
            e.Handled = true;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        if (e.Source is TextBox box)
        {
            OnTextBoxKey(viewModel, box, e);
            return;
        }

        if (e.KeyModifiers == KeyModifiers.None && e.Key is >= Key.D1 and <= Key.D9 or >= Key.NumPad1 and <= Key.NumPad9)
        {
            var number = e.Key >= Key.NumPad1 ? e.Key - Key.NumPad1 + 1 : e.Key - Key.D1 + 1;
            viewModel.SelectWorkFolderByNumberCommand.Execute(number);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && ItemsList.IsKeyboardFocusWithin)
        {
            viewModel.MoveCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None && ItemsList.IsKeyboardFocusWithin && viewModel.CanDelete)
        {
            viewModel.DeleteSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnTextBoxKey(SortingViewModel viewModel, TextBox box, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        if (box == NewFolderBox && e.Key == Key.Enter)
        {
            viewModel.ConfirmNewFolderCommand.Execute(null);
            e.Handled = true;
        }
        else if (box == NewFolderBox && e.Key == Key.Escape)
        {
            viewModel.CancelNewFolderCommand.Execute(null);
            e.Handled = true;
        }
        else if (box == FolderSearchBox && e.Key == Key.Enter && viewModel.FolderResults.Count > 0)
        {
            viewModel.ChooseFoundFolderCommand.Execute(viewModel.FolderResults[0]);
            e.Handled = true;
        }
    }
}

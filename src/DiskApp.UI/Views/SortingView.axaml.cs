using System.ComponentModel;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Views;

/// <summary>
/// Страница «Разбор»: выбор нескольких файлов в списке, <c>1…9</c> — рабочая папка, <c>Enter</c> в списке или
/// <c>Ctrl+Enter</c> где угодно — перенести, <c>Enter</c> в поле тега — добавить тег, в поле поиска — первая найденная
/// папка, в поле имени — создать подпапку, <c>Esc</c> — отменить ввод имени.
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
        FolderResultsList.SelectionChanged += (_, _) =>
        {
            if (FolderResultsList.SelectedItem is SortingFolderInfo folder)
            {
                ViewModel?.ChooseFoundFolderCommand.Execute(folder);
            }
        };
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
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
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SortingViewModel.IsNamingFolder) && _subscribed is { IsNamingFolder: true })
        {
            Dispatcher.UIThread.Post(() => NewFolderBox.Focus(), DispatcherPriority.Input);
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
    }

    private void OnTextBoxKey(SortingViewModel viewModel, TextBox box, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        if (box == TagBox && e.Key == Key.Enter)
        {
            viewModel.AddTagCommand.Execute(null);
            e.Handled = true;
        }
        else if (box == NewFolderBox && e.Key == Key.Enter)
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

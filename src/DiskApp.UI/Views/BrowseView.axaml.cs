using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

using DiskApp.UI.Models;
using DiskApp.UI.ViewModels;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.Views;

/// <summary>
/// Страница «Обзор»: клавиши как в Проводнике, двойной щелчок открывает, выбор в списке и плитке передаётся модели.
/// </summary>
public partial class BrowseView : UserControl
{
    /// <summary>
    /// Создаёт страницу и подключает клавиши, выбор и переходы.
    /// </summary>
    public BrowseView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        ItemsList.SelectionChanged += (_, _) => PushSelection(ItemsList);
        TilesList.SelectionChanged += (_, _) => PushSelection(TilesList);
        ItemsList.DoubleTapped += OnItemDoubleTapped;
        TilesList.DoubleTapped += OnItemDoubleTapped;
        FolderTree.SelectionChanged += OnTreeSelectionChanged;
        Breadcrumbs.ItemClicked += OnCrumbClicked;
    }

    private BrowseViewModel? ViewModel => DataContext as BrowseViewModel;

    /// <inheritdoc />
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        // Сведениям нужно место: на узком окне остаются дерево и содержимое.
        DetailsCard.IsVisible = e.NewSize.Width >= 1000;
    }

    private void PushSelection(ListBox list) => ViewModel?.UpdateSelection([.. list.SelectedItems?.OfType<SearchHit>() ?? []]);

    private void OnItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel is { } viewModel && (e.Source as Avalonia.Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is SearchHit hit)
        {
            viewModel.OpenItemCommand.Execute(hit);
        }
    }

    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is { } viewModel && FolderTree.SelectedItem is FolderNode node)
        {
            viewModel.NavigateNodeCommand.Execute(node);
        }
    }

    private void OnCrumbClicked(FABreadcrumbBar sender, FABreadcrumbBarItemClickedEventArgs args)
    {
        if (ViewModel is { } viewModel && args.Item is BrowseCrumb crumb)
        {
            viewModel.NavigateCrumbCommand.Execute(crumb);
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var inTextBox = e.Source is TextBox;
        var alt = e.KeyModifiers == KeyModifiers.Alt;
        var ctrl = e.KeyModifiers == KeyModifiers.Control;
        var none = e.KeyModifiers == KeyModifiers.None;
        System.Windows.Input.ICommand? command = e.Key switch
        {
            Key.L when ctrl => null,
            Key.F5 when none => viewModel.RefreshCommand,
            Key.Left when alt => viewModel.GoBackCommand,
            Key.Right when alt => viewModel.GoForwardCommand,
            Key.Up when alt => viewModel.GoUpCommand,
            Key.Back when none && !inTextBox => viewModel.GoBackCommand,
            Key.Enter when none && !inTextBox => viewModel.OpenItemCommand,
            Key.F2 when none && !inTextBox => viewModel.RenameCommand,
            Key.Delete when none && !inTextBox => viewModel.DeleteCommand,
            Key.C when ctrl && !inTextBox => viewModel.CopyFilesCommand,
            Key.C when e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) && !inTextBox => viewModel.CopyPathCommand,
            _ => null,
        };

        if (e.Key == Key.L && ctrl)
        {
            AddressBox.Focus();
            AddressBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (command is null)
        {
            return;
        }

        var parameter = e.Key == Key.Enter && viewModel.Selection.Count > 0 ? viewModel.Selection[0] : null;
        if (command.CanExecute(parameter))
        {
            command.Execute(parameter);
            e.Handled = true;
        }
    }
}

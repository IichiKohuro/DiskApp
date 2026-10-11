using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

using DiskApp.UI.Localization;
using DiskApp.UI.Models;
using DiskApp.UI.ViewModels;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.Views;

/// <summary>
/// Страница «Обзор»: клавиши как в Проводнике, двойной щелчок открывает, выбор в списке и плитке передаётся модели.
/// </summary>
public partial class BrowseView : UserControl
{
    private const double DragThreshold = 4;

    private (PointerPressedEventArgs Args, Point Origin)? _pressed;

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

        // Перетаскивание: из списка — наружу и на папки внутри, извне — на папки списка и дерева.
        foreach (var list in new[] { ItemsList, TilesList })
        {
            list.AddHandler(PointerPressedEvent, OnItemPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
            list.AddHandler(PointerMovedEvent, OnItemPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
            list.AddHandler(PointerReleasedEvent, (_, _) => _pressed = null, RoutingStrategies.Bubble, handledEventsToo: true);
        }

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
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

        if (!inTextBox && ctrl && e.Key == Key.V)
        {
            e.Handled = true;
            _ = PasteAsync(viewModel);
            return;
        }

        if (!inTextBox && ctrl && e.Key == Key.X)
        {
            e.Handled = true;
            viewModel.CutCommand.Execute(null);
            return;
        }

        if (e.Key == Key.N && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            e.Handled = true;
            viewModel.NewFolderCommand.Execute(null);
            return;
        }
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
            Key.D when ctrl && !inTextBox => viewModel.TogglePinCommand,
            Key.T when ctrl && !inTextBox => viewModel.EditTagsCommand,
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

    // Вставка: буфер «Обзора», а если он пуст — файлы из системного буфера обмена (скопированные в Проводнике).
    private async Task PasteAsync(BrowseViewModel viewModel)
    {
        IReadOnlyList<string>? system = null;
        if (!viewModel.HasClipboard && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            try
            {
                system = (await clipboard.TryGetFilesAsync())?.Select(static item => item.TryGetLocalPath()).OfType<string>().ToArray();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                system = null;
            }
        }

        await viewModel.PasteAsync(system);
    }

    private static string? FolderAt(object? source)
    {
        var visual = source as Visual;
        if (visual?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is SearchHit { Entry.Kind: FileKind.Folder } hit)
        {
            return hit.Entry.FullPath;
        }

        return visual?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext is FolderNode { IsPlaceholder: false } node ? node.Path : null;
    }

    private void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
        => _pressed = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null
            ? (e, e.GetPosition(this))
            : null;

    private void OnItemPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is not { } pressed || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var offset = e.GetPosition(this) - pressed.Origin;
        if (Math.Abs(offset.X) < DragThreshold && Math.Abs(offset.Y) < DragThreshold)
        {
            return;
        }

        _pressed = null;
        _ = StartDragAsync(pressed.Args);
    }

    private async Task StartDragAsync(PointerPressedEventArgs pressed)
    {
        if (ViewModel is not { } viewModel || viewModel.Selection.Count == 0 || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        try
        {
            var data = new DataTransfer();
            foreach (var hit in viewModel.Selection)
            {
                IStorageItem? item = hit.Entry.Kind == FileKind.Folder
                    ? await storage.TryGetFolderFromPathAsync(hit.Entry.FullPath)
                    : await storage.TryGetFileFromPathAsync(hit.Entry.FullPath);
                if (item is not null)
                {
                    data.Add(DataTransferItem.CreateFile(item));
                }
            }

            // Наружу — копия (Shift — перенос); внутрь «Обзора» решает OnDrop по модификатору.
            await DragDrop.DoDragDropAsync(pressed, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        catch (Exception exception)
        {
            ViewModel?.ReportError(L.T("Browse.CouldNotDrag"), exception);
        }
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        var folder = FolderAt(e.Source);
        e.DragEffects = folder is null || !e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.None
            : e.KeyModifiers.HasFlag(KeyModifiers.Control) ? DragDropEffects.Copy : DragDropEffects.Move;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel is not { } viewModel || FolderAt(e.Source) is not { } folder || e.DataTransfer.TryGetFiles() is not { } items)
        {
            return;
        }

        var paths = items.Select(static item => item.TryGetLocalPath()).OfType<string>().ToArray();
        _ = viewModel.TransferAsync(paths, folder, move: !e.KeyModifiers.HasFlag(KeyModifiers.Control));
    }
}

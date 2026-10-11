using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Views;

/// <summary>
/// Страница «Коллекции»: файлы, брошенные на список слева, добавляются в него, брошенные справа — в выбранный список;
/// <c>Enter</c> и двойной щелчок открывают запись, <c>Alt+↑/↓</c> меняют порядок, <c>Del</c> убирает запись из списка или
/// удаляет выбранную коллекцию, <c>F2</c> переименовывает её, <c>Ctrl+T</c> — теги записи, <c>F5</c> — обновить.
/// </summary>
public partial class CollectionsView : UserControl
{
    /// <summary>
    /// Создаёт представление.
    /// </summary>
    public CollectionsView()
    {
        InitializeComponent();
        FilesList.DoubleTapped += (_, _) => ViewModel?.OpenFileCommand.Execute(null);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private CollectionsViewModel? ViewModel => DataContext as CollectionsViewModel;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel || e.Source is TextBox)
        {
            return;
        }

        var inFiles = FilesList.IsKeyboardFocusWithin;
        var inLists = ListsBox.IsKeyboardFocusWithin;
        System.Windows.Input.ICommand? command = (e.Key, e.KeyModifiers) switch
        {
            (Key.Enter, KeyModifiers.None) when inFiles => viewModel.OpenFileCommand,
            (Key.T, KeyModifiers.Control) when inFiles => viewModel.EditFileTagsCommand,
            (Key.Up, KeyModifiers.Alt) when inFiles => viewModel.MoveUpCommand,
            (Key.Down, KeyModifiers.Alt) when inFiles => viewModel.MoveDownCommand,
            (Key.Delete, KeyModifiers.None) when inFiles => viewModel.RemoveFileCommand,
            (Key.Delete, KeyModifiers.None) when inLists => viewModel.DeleteCommand,
            (Key.F2, KeyModifiers.None) when inLists => viewModel.RenameCommand,
            (Key.F5, KeyModifiers.None) => viewModel.RefreshCommand,
            _ => null,
        };

        if (command?.CanExecute(null) == true)
        {
            e.Handled = true;
            command.Execute(null);
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var target = TargetOf(e);
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) && target?.IsEditable == true ? DragDropEffects.Link : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel is not { } viewModel || TargetOf(e) is not { IsEditable: true } target || e.DataTransfer.TryGetFiles() is not { } items)
        {
            return;
        }

        var paths = items.Select(static item => item.TryGetLocalPath()).OfType<string>().ToArray();
        if (paths.Length > 0)
        {
            e.Handled = true;
            _ = viewModel.AddPathsAsync(paths, target);
        }
    }

    /// <summary>Куда бросают: строка списка слева или выбранный список, если бросают на записи справа.</summary>
    private CollectionListItem? TargetOf(DragEventArgs e)
    {
        if (e.Source is Avalonia.Visual visual && visual.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: CollectionListItem item })
        {
            return item;
        }

        return e.Source is Avalonia.Visual source && source.FindAncestorOfType<ListBox>(includeSelf: true) == FilesList ? ViewModel?.SelectedList : null;
    }
}

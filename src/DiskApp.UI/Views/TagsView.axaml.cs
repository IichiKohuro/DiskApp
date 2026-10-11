using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Views;

/// <summary>
/// Страница «Теги»: двойной щелчок и <c>Enter</c> открывают файл, <c>Ctrl+T</c> — теги файла, <c>F2</c> и <c>Del</c> —
/// изменить и удалить выбранный тег, <c>F5</c> — обновить.
/// </summary>
public partial class TagsView : UserControl
{
    /// <summary>
    /// Создаёт представление.
    /// </summary>
    public TagsView()
    {
        InitializeComponent();
        FilesList.DoubleTapped += (_, _) => ViewModel?.OpenFileCommand.Execute(null);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private TagsViewModel? ViewModel => DataContext as TagsViewModel;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel || e.Source is TextBox)
        {
            return;
        }

        var inFiles = FilesList.IsKeyboardFocusWithin;
        var inTags = TagsList.IsKeyboardFocusWithin;
        System.Windows.Input.ICommand? command = (e.Key, e.KeyModifiers) switch
        {
            (Key.Enter, KeyModifiers.None) when inFiles => viewModel.OpenFileCommand,
            (Key.T, KeyModifiers.Control) when inFiles => viewModel.EditFileTagsCommand,
            (Key.F2, KeyModifiers.None) when inTags => viewModel.EditTagCommand,
            (Key.Delete, KeyModifiers.None) when inTags => viewModel.DeleteTagCommand,
            (Key.F5, KeyModifiers.None) => viewModel.RefreshCommand,
            _ => null,
        };

        if (command?.CanExecute(null) == true)
        {
            e.Handled = true;
            command.Execute(null);
        }
    }
}

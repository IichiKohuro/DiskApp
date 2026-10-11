using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Views;

/// <summary>
/// Содержимое диалога тегов: фокус в поле нового тега, <c>Enter</c> в нём создаёт тег, а не закрывает диалог.
/// </summary>
public partial class TagPickerView : UserControl
{
    /// <summary>
    /// Создаёт представление.
    /// </summary>
    public TagPickerView()
    {
        InitializeComponent();
        NewTagBox.AddHandler(KeyDownEvent, OnNewTagKeyDown, RoutingStrategies.Tunnel);
    }

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        NewTagBox.Focus();
    }

    private void OnNewTagKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not TagPickerViewModel viewModel || viewModel.NewTagName.Trim().Length == 0)
        {
            return;
        }

        e.Handled = true;
        viewModel.AddTagCommand.Execute(null);
    }
}

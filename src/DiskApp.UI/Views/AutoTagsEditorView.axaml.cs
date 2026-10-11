using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Views;

/// <summary>
/// Поле «Авто-теги» (PLAN.md, этап 19): <c>Enter</c> в поле ввода добавляет тег, а не нажимает кнопку диалога.
/// </summary>
public partial class AutoTagsEditorView : UserControl
{
    /// <summary>
    /// Создаёт представление.
    /// </summary>
    public AutoTagsEditorView()
    {
        InitializeComponent();
        TagBox.AddHandler(KeyDownEvent, OnTagKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnTagKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not AutoTagsEditorViewModel viewModel || viewModel.Text.Trim().Length == 0)
        {
            return;
        }

        e.Handled = true;
        viewModel.AddCommand.Execute(null);
    }
}

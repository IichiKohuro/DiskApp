using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Views;

/// <summary>
/// Страница настроек: пока записывается сочетание клавиш, нажатия уходят в запись, а не в элементы страницы;
/// <c>Enter</c> в поле нового исключения добавляет правило.
/// </summary>
public partial class SettingsView : UserControl
{
    /// <summary>
    /// Создаёт страницу и загружает её разметку.
    /// </summary>
    public SettingsView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        NewExclusionBox.AddHandler(KeyDownEvent, OnExclusionKeyDown, RoutingStrategies.Tunnel);
    }

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is { RecordingTarget: not null } viewModel && viewModel.RecordKey(e.Key, e.KeyModifiers))
        {
            e.Handled = true;
        }
    }

    private void OnExclusionKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel is { } viewModel && viewModel.AddExclusionCommand.CanExecute(null))
        {
            e.Handled = true;
            viewModel.AddExclusionCommand.Execute(null);
        }
    }
}

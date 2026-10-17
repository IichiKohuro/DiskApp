using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

using DiskApp.UI.Localization;
using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Views;

/// <summary>
/// Страница «Проекты» (PLAN.md, этап 24): у нового проекта курсор сразу в поле номера; «Выбрать папку» открывает
/// меню из рабочих папок «Разбора» и «Другая папка…».
/// </summary>
public partial class ProjectsView : UserControl
{
    private ProjectsViewModel? _viewModel;

    /// <summary>Создаёт представление.</summary>
    public ProjectsView() => InitializeComponent();

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.NumberFocusRequested -= OnNumberFocusRequested;
        }

        _viewModel = DataContext as ProjectsViewModel;
        if (_viewModel is not null)
        {
            _viewModel.NumberFocusRequested += OnNumberFocusRequested;
        }

        base.OnDataContextChanged(e);
    }

    // Поле становится видимым только после привязок — фокус ставится следующим кадром.
    private void OnNumberFocusRequested(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => NumberBox.Focus(), DispatcherPriority.Background);

    private void OnChooseFolderClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not { } viewModel)
        {
            return;
        }

        var menu = new MenuFlyout();
        if (viewModel.HasWorkFolders)
        {
            menu.Items.Add(new MenuItem { Header = L.T("Projects.WorkFolders"), IsEnabled = false });
            foreach (var folder in viewModel.WorkFolders)
            {
                menu.Items.Add(new MenuItem { Header = folder.Title, Command = viewModel.ChooseFolderCommand, CommandParameter = folder.Path });
            }

            menu.Items.Add(new Separator());
        }

        menu.Items.Add(new MenuItem { Header = L.T("Projects.OtherFolder"), Command = viewModel.BrowseFolderCommand });
        menu.ShowAt(ChooseFolderButton);
    }
}

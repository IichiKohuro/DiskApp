using Avalonia.Controls;
using Avalonia.Threading;

using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Views;

/// <summary>
/// Страница «Заметки» (PLAN.md, этап 23): флажок, отмеченный в просмотре Markdown, меняет текст заметки; у новой заметки
/// курсор сразу в поле текста.
/// </summary>
public partial class NotesView : UserControl
{
    private NotesViewModel? _viewModel;

    /// <summary>
    /// Создаёт представление.
    /// </summary>
    public NotesView()
    {
        InitializeComponent();
        Preview.TaskToggled += (_, toggle) => _viewModel?.ToggleTask(toggle);
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.EditorFocusRequested -= OnEditorFocusRequested;
        }

        _viewModel = DataContext as NotesViewModel;
        if (_viewModel is not null)
        {
            _viewModel.EditorFocusRequested += OnEditorFocusRequested;
        }

        base.OnDataContextChanged(e);
    }

    // Поле становится видимым только после привязок — фокус ставится следующим кадром.
    private void OnEditorFocusRequested(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => Editor.Focus(), DispatcherPriority.Background);
}

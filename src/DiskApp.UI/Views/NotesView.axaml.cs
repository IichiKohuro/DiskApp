using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

using DiskApp.UI.Text;
using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Views;

/// <summary>
/// Страница «Заметки» (PLAN.md, этап 23): флажок, отмеченный в просмотре Markdown, меняет текст заметки; у новой заметки
/// курсор сразу в поле текста. Панель форматирования и <c>Ctrl+B</c>, <c>Ctrl+I</c>, <c>Ctrl+K</c> правят выделение
/// в поле; <c>Enter</c> в пункте списка продолжает список.
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
        ListSplitter.DragCompleted += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.ListColumnWidth = RootGrid.ColumnDefinitions[0].Width;
            }
        };
        Editor.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
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
            RootGrid.ColumnDefinitions[0].Width = _viewModel.ListColumnWidth;
        }

        base.OnDataContextChanged(e);
    }

    // Поле становится видимым только после привязок — фокус ставится следующим кадром.
    private void OnEditorFocusRequested(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => Editor.Focus(), DispatcherPriority.Background);

    private void OnFormatClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<MarkdownFormat>(tag, out var format))
        {
            Format(format);
        }
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers == KeyModifiers.Control && e.Key is Key.B or Key.I or Key.K)
        {
            Format(e.Key switch { Key.B => MarkdownFormat.Bold, Key.I => MarkdownFormat.Italic, _ => MarkdownFormat.Link });
            e.Handled = true;
        }
        else if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.Enter && Editor.SelectionStart == Editor.SelectionEnd
            && MarkdownEditing.ContinueList(Editor.Text, Editor.CaretIndex) is { } edit)
        {
            Apply(edit);
            e.Handled = true;
        }
    }

    private void Format(MarkdownFormat format) => Apply(MarkdownEditing.Apply(Editor.Text, Editor.SelectionStart, Editor.SelectionEnd, format));

    // Правка — заменой выделенного участка: так её можно отменить Ctrl+Z, как обычный ввод.
    private void Apply(MarkdownEdit edit)
    {
        Editor.SelectionStart = edit.Start;
        Editor.SelectionEnd = edit.Start + edit.Length;
        Editor.SelectedText = edit.Replacement;
        Editor.SelectionStart = edit.SelectionStart;
        Editor.SelectionEnd = edit.SelectionEnd;
        Editor.CaretIndex = edit.SelectionEnd;
        Editor.Focus();
    }
}

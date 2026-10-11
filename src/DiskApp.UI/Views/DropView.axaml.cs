using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Views;

/// <summary>
/// Содержимое окна Drag&amp;Drop (PLAN.md, этап 18): заголовок с кнопкой закрытия, область «Бросить в накопитель», итог и
/// подсказка. Область принимает файлы и папки: пока их несут, подпись показывает действие («Переместить в „Загрузки“»),
/// при броске модель ставит перенос или копию. Действия, которые разрешает источник, приходят в <see cref="DragEventArgs.DragEffects"/>.
/// </summary>
public partial class DropView : UserControl
{
    private IReadOnlyList<string> _dragged = [];
    private DragDropEffects _allowed;

    /// <summary>
    /// Создаёт представление.
    /// </summary>
    public DropView()
    {
        InitializeComponent();
        InboxZone.AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        InboxZone.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        InboxZone.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        InboxZone.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <summary>Над областью несут файлы: окну не прятаться само.</summary>
    public event EventHandler? DragStarted;

    private DropWindowViewModel? ViewModel => DataContext as DropWindowViewModel;

    private static IReadOnlyList<string> LocalPaths(DragEventArgs e)
        => e.DataTransfer.TryGetFiles() is { } items ? [.. items.Select(static item => item.TryGetLocalPath()).OfType<string>()] : [];

    private static DragDropEffects EffectOf(DropDecision decision) => decision.Mode switch
    {
        DropMode.Move => DragDropEffects.Move,
        DropMode.Copy => DragDropEffects.Copy,
        _ => DragDropEffects.None,
    };

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        // Список файлов и разрешённые действия читаются один раз: при движении меняются только модификаторы.
        _dragged = LocalPaths(e);
        _allowed = e.DragEffects;
        DragStarted?.Invoke(this, EventArgs.Empty);
        Preview(e);
    }

    private void OnDragOver(object? sender, DragEventArgs e) => Preview(e);

    private void OnDragLeave(object? sender, DragEventArgs e) => ViewModel?.EndPreview();

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var paths = _dragged.Count > 0 ? _dragged : LocalPaths(e);
        var allowed = _allowed == DragDropEffects.None ? e.DragEffects : _allowed;
        _dragged = [];
        if (ViewModel is { } viewModel)
        {
            e.DragEffects = EffectOf(DropWindowViewModel.Decide(paths, viewModel.Inbox?.Path, allowed, e.KeyModifiers));
            _ = viewModel.DropAsync(paths, allowed, e.KeyModifiers);
        }
    }

    private void Preview(DragEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = EffectOf(viewModel.PreviewDrop(_dragged, _allowed, e.KeyModifiers));
    }
}

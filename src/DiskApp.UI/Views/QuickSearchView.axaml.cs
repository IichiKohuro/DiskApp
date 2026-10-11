using System.ComponentModel;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

using DiskApp.UI.Input;
using DiskApp.UI.Models;
using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Views;

/// <summary>
/// Содержимое быстрого поиска, общее для всплывающего окна и страницы «Поиск».
/// Клавиши разбирает <see cref="QuickSearchKeyMap"/>; меню строки — по правой кнопке, <c>Tab</c> или <c>Shift+F10</c>.
/// </summary>
public partial class QuickSearchView : UserControl
{
    private const double DragThreshold = 4;

    private QuickSearchViewModel? _subscribed;
    private (PointerPressedEventArgs Args, Point Origin, SearchHit Hit)? _pressed;

    /// <summary>
    /// Создаёт представление и подключает обработку клавиш, двойного щелчка и контекстного меню.
    /// </summary>
    public QuickSearchView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        ResultsList.DoubleTapped += OnResultDoubleTapped;
        ResultsList.ContainerPrepared += OnContainerPrepared;
        if (ResultsList.ContextFlyout is { } menu)
        {
            menu.Closed += (_, _) => FocusQueryKeepingText();
        }

        // Перетаскивание из результатов: строка выбирается на нажатии, перетаскивание начинается после сдвига.
        ResultsList.AddHandler(PointerPressedEvent, OnRowPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        ResultsList.AddHandler(PointerMovedEvent, OnRowPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        ResultsList.AddHandler(PointerReleasedEvent, (_, _) => _pressed = null, RoutingStrategies.Bubble, handledEventsToo: true);

        // Файлы извне: на папку в результатах.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private QuickSearchViewModel? ViewModel => DataContext as QuickSearchViewModel;

    /// <summary>
    /// Ставит фокус в поле ввода и выделяет текст, чтобы новый запрос заменил старый.
    /// </summary>
    public void FocusQuery()
    {
        QueryBox.Focus();
        QueryBox.SelectAll();
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_subscribed is not null)
        {
            _subscribed.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribed.QueryReplaced -= OnQueryReplaced;
        }

        _subscribed = ViewModel;
        if (_subscribed is not null)
        {
            _subscribed.PropertyChanged += OnViewModelPropertyChanged;
            _subscribed.QueryReplaced += OnQueryReplaced;
        }
    }

    // Чип, подсказка или история заменили текст: курсор — в конец, чтобы продолжить ввод.
    private void OnQueryReplaced(object? sender, EventArgs e) => Dispatcher.UIThread.Post(FocusQueryKeepingText);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(QuickSearchViewModel.PendingAction) || ViewModel is not { } viewModel)
        {
            return;
        }

        // Поле имени появляется после применения привязок: фокус и выделение имени без расширения — следующим кадром.
        Dispatcher.UIThread.Post(() =>
        {
            if (viewModel.IsRenaming)
            {
                RenameBox.Focus();
                RenameBox.SelectionStart = 0;
                RenameBox.SelectionEnd = viewModel.RenameSelectionLength;
            }
            else
            {
                FocusQueryKeepingText();
            }
        });
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        if (viewModel.HasPendingAction)
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
            {
                e.Handled = true;
                _ = viewModel.ConfirmPendingActionAsync();
                return;
            }

            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                viewModel.CancelPendingAction();
                return;
            }

            if (viewModel.IsRenaming)
            {
                // Остальные клавиши — полю нового имени.
                return;
            }

            // Подтверждение корзины: любая другая клавиша закрывает панель и работает как обычно.
            viewModel.CancelPendingAction();
        }

        var text = QueryBox.Text ?? string.Empty;
        var box = new QueryBoxState(
            IsEmpty: text.Length == 0,
            HasSelection: QueryBox.SelectionStart != QueryBox.SelectionEnd,
            CaretAtEnd: QueryBox.CaretIndex >= text.Length,
            HasSuggestions: text.Length > 0 && viewModel.ShowSuggestions);
        if (QuickSearchKeyMap.Resolve(e.Key, e.KeyModifiers, box) is not { } key)
        {
            return;
        }

        e.Handled = true;
        if (key.Command == QuickSearchCommand.ContextMenu)
        {
            ShowContextMenu(viewModel);
            return;
        }

        _ = ExecuteAsync(viewModel, key);
    }

    private async Task ExecuteAsync(QuickSearchViewModel viewModel, QuickSearchKey key)
    {
        await viewModel.ExecuteAsync(key.Command, key.Number);
        ScrollToSelection(viewModel);
    }

    private void ShowContextMenu(QuickSearchViewModel viewModel)
    {
        if (viewModel.SelectedRow is not { } row || ResultsList.ContextFlyout is not { } menu)
        {
            return;
        }

        ResultsList.ScrollIntoView(row);
        var target = ResultsList.ContainerFromItem(row) ?? (Control)ResultsList;
        menu.ShowAt(target);
    }

    // Первая строка — к началу списка, чтобы над ней был виден заголовок секции.
    private void ScrollToSelection(QuickSearchViewModel viewModel)
    {
        if (viewModel.SelectedIndex == 0)
        {
            ResultsList.ScrollIntoView(0);
        }
        else if (viewModel.SelectedRow is { } row)
        {
            ResultsList.ScrollIntoView(row);
        }
    }

    private void FocusQueryKeepingText()
    {
        QueryBox.Focus();
        QueryBox.CaretIndex = QueryBox.Text?.Length ?? 0;
    }

    private static SearchHit? HitAt(object? source)
        => (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as SearchHit;

    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressed = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && HitAt(e.Source) is { } hit
            ? (e, e.GetPosition(this), hit)
            : null;
    }

    private void OnRowPointerMoved(object? sender, PointerEventArgs e) => _ = StartDragAsync(e);

    private async Task StartDragAsync(PointerEventArgs e)
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
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage || pressed.Hit.Entry.IsOffline)
        {
            return;
        }

        try
        {
            var path = pressed.Hit.Entry.FullPath;
            IStorageItem? item = pressed.Hit.Entry.Kind == FileKind.Folder
                ? await storage.TryGetFolderFromPathAsync(path)
                : await storage.TryGetFileFromPathAsync(path);
            if (item is null)
            {
                return;
            }

            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateFile(item));
            var effect = await DragDrop.DoDragDropAsync(pressed.Args, data, QuickSearchViewModel.DragEffectsFor(e.KeyModifiers));

            // Файл перенесли в другое место: список перечитывается, чтобы строка не указывала в пустоту.
            if (effect == DragDropEffects.Move && ViewModel is { } viewModel)
            {
                await viewModel.ActivateAsync();
            }
        }
        catch (Exception exception)
        {
            // Обработчик событий: исключение отсюда уронило бы приложение — сообщаем и продолжаем.
            ViewModel?.ReportError("Не удалось перетащить", exception);
        }
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (ViewModel is { } viewModel && e.DataTransfer.Contains(DataFormat.File) && _pressed is null)
        {
            viewModel.ShowDropHint = true;
        }
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.ShowDropHint = false;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var target = HitAt(e.Source);
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) && QuickSearchViewModel.CanDropInto(target)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        if (e.DragEffects != DragDropEffects.None && ViewModel is { } viewModel)
        {
            // Подсветка цели: строка-папка под курсором становится выбранной.
            viewModel.SelectedRow = target;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e) => _ = AcceptDropAsync(e);

    private async Task AcceptDropAsync(DragEventArgs e)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        viewModel.ShowDropHint = false;
        if (HitAt(e.Source) is not { } target || !QuickSearchViewModel.CanDropInto(target) || e.DataTransfer.TryGetFiles() is not { } items)
        {
            return;
        }

        try
        {
            var files = items.OfType<IStorageFile>().Select(static file => file.TryGetLocalPath()).OfType<string>().ToArray();
            var folders = items.OfType<IStorageFolder>().Count();
            await viewModel.CopyIntoAsync(target, files, folders);
        }
        catch (Exception exception)
        {
            viewModel.ReportError("Не удалось принять файлы", exception);
        }
    }

    // Контейнеры переиспользуются при прокрутке: класс заголовка ставится и снимается каждый раз.
    private static void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
        => e.Container.Classes.Set("header", e.Container.DataContext is ResultSectionHeader);

    private void OnResultDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel is { } viewModel)
        {
            _ = viewModel.OpenSelectedAsync();
        }
    }
}

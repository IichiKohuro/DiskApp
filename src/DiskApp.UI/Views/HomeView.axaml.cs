using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

using DiskApp.UI.Models;
using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Views;

/// <summary>
/// Главная страница: приветствие, состояние индекса, корни, быстрые действия и списки файлов.
/// Двойной щелчок по файлу открывает его; папки из Проводника, брошенные на страницу, добавляются в индекс.
/// </summary>
public partial class HomeView : UserControl
{
    /// <summary>
    /// Создаёт страницу и подключает открытие файлов и перетаскивание папок.
    /// </summary>
    public HomeView()
    {
        InitializeComponent();
        AddHandler(DoubleTappedEvent, OnFileDoubleTapped);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private HomeViewModel? ViewModel => DataContext as HomeViewModel;

    private void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel is { } viewModel
            && (e.Source as Avalonia.Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is SearchHit hit)
        {
            viewModel.OpenFileCommand.Execute(hit);
        }
    }

    private void OnAddRootClick(object? sender, RoutedEventArgs e) => _ = PickRootsAsync();

    private async Task PickRootsAsync()
    {
        if (ViewModel is not { } viewModel || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        try
        {
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Папки, по которым искать",
                AllowMultiple = true,
            });
            var paths = folders.Select(static folder => folder.TryGetLocalPath()).OfType<string>().ToArray();
            if (paths.Length > 0)
            {
                await viewModel.AddRootsAsync(paths);
            }
        }
        catch (Exception exception)
        {
            viewModel.ReportError("Не удалось добавить папку", exception);
        }
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
        => e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Link : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel is not { } viewModel || e.DataTransfer.TryGetFiles() is not { } items)
        {
            return;
        }

        var folders = items.OfType<IStorageFolder>().Select(static folder => folder.TryGetLocalPath()).OfType<string>().ToArray();
        if (folders.Length == 0)
        {
            viewModel.ReportError("Нужна папка", new InvalidOperationException("Перетащите папку, чтобы добавить её в индекс; файлы сюда не копируются."));
            return;
        }

        _ = viewModel.AddRootsAsync(folders);
    }
}

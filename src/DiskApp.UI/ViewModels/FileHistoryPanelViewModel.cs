using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using DiskApp.Contracts.History;
using DiskApp.UI.Localization;
using DiskApp.UI.Services;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.ViewModels;

/// <summary>
/// «История» в карточке файла (PLAN.md, этап 20.4): события выбранного файла через переносы и переименования, с
/// источником. Свёрнута по умолчанию и читается, только когда раскрыта; «Вернуть на место» и «Восстановить» — здесь же.
/// </summary>
public sealed partial class FileHistoryPanelViewModel : ObservableObject
{
    private readonly IFileHistoryService _history;
    private readonly IInfoBarService? _notices;
    private int _version;

    /// <summary>Создаёт панель; без диспетчера новые записи истории не перечитываются сами.</summary>
    public FileHistoryPanelViewModel(IFileHistoryService history, IInfoBarService? notices = null, IDispatcher? dispatcher = null)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _notices = notices;
        if (dispatcher is not null)
        {
            // Записи приходят пачками раз в полсекунды: раскрытая панель просто перечитывается.
            _history.Changed += (_, _) => dispatcher.Post(() =>
            {
                if (IsExpanded)
                {
                    Reload();
                }
            });
        }
    }

    /// <summary>События файла, новые сверху.</summary>
    public ObservableCollection<FileHistoryItemViewModel> Items { get; } = [];

    /// <summary>Путь выбранного файла; <c>null</c> — ничего не выбрано.</summary>
    [ObservableProperty]
    public partial string? Path { get; set; }

    /// <summary>Панель раскрыта.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>Идёт загрузка.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoading { get; set; }

    /// <summary>Событий нет.</summary>
    public bool IsEmpty => !IsLoading && Items.Count == 0;

    /// <summary>Задача последней загрузки (тесты).</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    partial void OnPathChanged(string? value) => Reload();

    partial void OnIsExpandedChanged(bool value) => Reload();

    // «Вернуть на место» или «Восстановить» — команда строки.
    private async Task RevertAsync(FileHistoryItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            await _history.RevertAsync(item.Event.Id);
            _notices?.Show(item.Event.Kind == FileEventKind.Recycled ? L.T("FileHistory.Restored") : L.T("FileHistory.PutBackDone"), item.Name, FAInfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show(L.T("FileHistory.CouldNotRevert"), exception.Message, FAInfoBarSeverity.Error);
        }
    }

    private void Reload()
    {
        var version = ++_version;
        Items.Clear();
        OnPropertyChanged(nameof(IsEmpty));
        Loading = IsExpanded && Path is { Length: > 0 } path ? LoadAsync(path, version) : Task.CompletedTask;
    }

    private async Task LoadAsync(string path, int version)
    {
        IsLoading = true;
        try
        {
            var items = await _history.GetOfPathAsync(path);
            if (version != _version)
            {
                return;
            }

            foreach (var item in items)
            {
                Items.Add(new FileHistoryItemViewModel(item, RevertAsync));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // История не прочиталась — панель пустая, остальная карточка работает.
            _ = exception;
        }
        finally
        {
            if (version == _version)
            {
                IsLoading = false;
            }

            OnPropertyChanged(nameof(IsEmpty));
        }
    }
}

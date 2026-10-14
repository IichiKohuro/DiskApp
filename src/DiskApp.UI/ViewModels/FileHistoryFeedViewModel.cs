using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DiskApp.Contracts.History;
using DiskApp.UI.Localization;
using DiskApp.UI.Services;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.ViewModels;

/// <summary>
/// Лента истории файлов на странице «История» (PLAN.md, этап 20.4): события по дням, новые сверху, с отбором по папке
/// (корню), виду и источнику и поиском по имени — в том числе прежнему; «Вернуть на место» и «Восстановить»; новые
/// записи приходят сами, пока страница открыта.
/// </summary>
public sealed partial class FileHistoryFeedViewModel : ObservableObject
{
    /// <summary>Событий на страницу ленты.</summary>
    public const int PageSize = 100;

    private readonly IFileHistoryService _history;
    private readonly IRootsService? _roots;
    private readonly IInfoBarService? _notices;
    private readonly IDispatcher _dispatcher;
    private readonly Func<DateTimeOffset> _clock;
    private readonly List<FileHistoryEvent> _loaded = [];
    private CancellationTokenSource? _reload;
    private long? _nextBefore;
    private bool _active;
    private bool _stale = true;
    private bool _suppress;

    /// <summary>Создаёт ленту; без корней отбор по папке — только «Все папки», без уведомлений ошибки видны в строке.</summary>
    public FileHistoryFeedViewModel(IFileHistoryService history, IDispatcher dispatcher, IRootsService? roots = null, IInfoBarService? notices = null, Func<DateTimeOffset>? clock = null)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _roots = roots;
        _notices = notices;
        _clock = clock ?? (static () => DateTimeOffset.Now);
        Folders.Add(AllFolders);
        SelectedFolder = AllFolders;
        SelectedKind = KindFilters[0];
        SelectedSource = SourceFilters[0];
        _history.Changed += (_, _) => _dispatcher.Post(OnChanged);
    }

    /// <summary>Виды событий для отбора.</summary>
    public static IReadOnlyList<SettingOption<FileEventKind[]>> KindFilters { get; } =
    [
        new([], "FileHistory.Filter.AllKinds"),
        new([FileEventKind.Moved, FileEventKind.Renamed], "FileHistory.Filter.Moves"),
        new([FileEventKind.Recycled, FileEventKind.Deleted, FileEventKind.Restored], "FileHistory.Filter.Deleted"),
        new([FileEventKind.Created], "FileHistory.Filter.Created"),
        new([FileEventKind.TagsChanged], "FileHistory.Filter.Tags"),
    ];

    /// <summary>Источники для отбора.</summary>
    public static IReadOnlyList<SettingOption<FileEventSource[]>> SourceFilters { get; } =
    [
        new([], "FileHistory.Filter.AllSources"),
        new([FileEventSource.DiskApp], "FileHistory.Source.DiskApp"),
        new([FileEventSource.Sorting, FileEventSource.AutoSort], "FileHistory.Filter.Sorting"),
        new([FileEventSource.Explorer], "FileHistory.Source.Explorer"),
        new([FileEventSource.Reconcile], "FileHistory.Source.Reconcile"),
    ];

    /// <summary>Пауза перед поиском после ввода и перед обновлением по сигналу о новых записях.</summary>
    public TimeSpan ReloadDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Папки для отбора: «Все папки» и корни.</summary>
    public ObservableCollection<SettingOption<string?>> Folders { get; } = [];

    /// <summary>Дни ленты, новые сверху.</summary>
    public ObservableCollection<FileHistoryDayViewModel> Days { get; } = [];

    /// <summary>Выбранная папка.</summary>
    [ObservableProperty]
    public partial SettingOption<string?> SelectedFolder { get; set; }

    /// <summary>Выбранный вид событий.</summary>
    [ObservableProperty]
    public partial SettingOption<FileEventKind[]> SelectedKind { get; set; }

    /// <summary>Выбранный источник.</summary>
    [ObservableProperty]
    public partial SettingOption<FileEventSource[]> SelectedSource { get; set; }

    /// <summary>Поиск по имени и прежнему имени.</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>Есть ещё события ниже.</summary>
    [ObservableProperty]
    public partial bool HasMore { get; set; }

    /// <summary>Идёт загрузка.</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>Почему лента не загрузилась; пусто — всё хорошо.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string Error { get; set; } = string.Empty;

    /// <summary>Есть ошибка загрузки.</summary>
    public bool HasError => Error.Length > 0;

    /// <summary>Событий нет: пустое состояние.</summary>
    public bool IsEmpty => _loaded.Count == 0 && !IsLoading && !HasError;

    /// <summary>Пусто из-за отбора: подсказка «измените отбор».</summary>
    public bool IsFiltered => SelectedFolder.Value is not null || SelectedKind.Value.Length > 0 || SelectedSource.Value.Length > 0 || SearchText.Trim().Length > 0;

    private static SettingOption<string?> AllFolders { get; } = new(null, "FileHistory.Filter.AllFolders");

    /// <summary>Страница открыта: перечитать, если что-то изменилось, и следить за новыми записями.</summary>
    public async Task ActivateAsync()
    {
        _active = true;
        await LoadFoldersAsync();
        if (_stale)
        {
            await ReloadAsync();
        }
    }

    /// <summary>Страница закрыта: новые записи только помечают ленту устаревшей.</summary>
    public void Deactivate() => _active = false;

    /// <summary>Перечитывает ленту с начала по текущему отбору.</summary>
    public async Task ReloadAsync()
    {
        _stale = false;
        CancelPendingReload();
        _loaded.Clear();
        _nextBefore = null;
        await LoadPageAsync();
    }

    /// <summary>Следующая страница ленты.</summary>
    [RelayCommand]
    private Task LoadMoreAsync() => HasMore ? LoadPageAsync() : Task.CompletedTask;

    /// <summary>Перечитать.</summary>
    [RelayCommand]
    private Task RefreshAsync() => ReloadAsync();

    /// <summary>Сбросить отбор.</summary>
    [RelayCommand]
    private Task ClearFiltersAsync()
    {
        _suppress = true;
        try
        {
            SelectedFolder = AllFolders;
            SelectedKind = KindFilters[0];
            SelectedSource = SourceFilters[0];
            SearchText = string.Empty;
        }
        finally
        {
            _suppress = false;
        }

        return ReloadAsync();
    }

    // «Вернуть на место» или «Восстановить» — команда строки.
    private async Task RevertAsync(FileHistoryItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            await _history.RevertAsync(item.Event.Id);
            _notices?.Show(
                item.Event.Kind == FileEventKind.Recycled ? L.T("FileHistory.Restored") : L.T("FileHistory.PutBackDone"),
                item.Name,
                FAInfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show(L.T("FileHistory.CouldNotRevert"), exception.Message, FAInfoBarSeverity.Error);
        }
    }

    partial void OnSelectedFolderChanged(SettingOption<string?> value) => FilterChanged(immediately: true);

    partial void OnSelectedKindChanged(SettingOption<FileEventKind[]> value) => FilterChanged(immediately: true);

    partial void OnSelectedSourceChanged(SettingOption<FileEventSource[]> value) => FilterChanged(immediately: true);

    partial void OnSearchTextChanged(string value) => FilterChanged(immediately: false);

    private void FilterChanged(bool immediately)
    {
        OnPropertyChanged(nameof(IsFiltered));
        if (_suppress || !_active)
        {
            _stale = true;
            return;
        }

        if (immediately)
        {
            _ = ReloadAsync();
        }
        else
        {
            ScheduleReload();
        }
    }

    private void OnChanged()
    {
        if (_active)
        {
            ScheduleReload();
        }
        else
        {
            _stale = true;
        }
    }

    private void ScheduleReload()
    {
        CancelPendingReload();
        var cts = new CancellationTokenSource();
        _reload = cts;
        _ = DelayedReloadAsync(cts.Token);
    }

    private async Task DelayedReloadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ReloadDelay, cancellationToken);
            await _dispatcher.InvokeAsync(ReloadAsync);
        }
        catch (OperationCanceledException)
        {
            // Пришёл новый ввод.
        }
    }

    private void CancelPendingReload()
    {
        _reload?.Cancel();
        _reload?.Dispose();
        _reload = null;
    }

    private async Task LoadFoldersAsync()
    {
        if (_roots is null)
        {
            return;
        }

        try
        {
            var roots = await _roots.GetRootsAsync();
            var selected = SelectedFolder.Value;
            _suppress = true;
            try
            {
                for (var i = Folders.Count - 1; i > 0; i--)
                {
                    Folders.RemoveAt(i);
                }

                foreach (var root in roots.OrderBy(static root => root.Label, StringComparer.CurrentCultureIgnoreCase))
                {
                    Folders.Add(new SettingOption<string?>(root.Path, root.Label));
                }

                SelectedFolder = Folders.FirstOrDefault(option => option.Value == selected) ?? AllFolders;
            }
            finally
            {
                _suppress = false;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Корни не прочитались — отбор только «Все папки».
            _ = exception;
        }
    }

    private async Task LoadPageAsync()
    {
        IsLoading = true;
        Error = string.Empty;
        try
        {
            var text = SearchText.Trim();
            var page = await _history.GetAsync(new FileHistoryFilter
            {
                Folder = SelectedFolder.Value,
                Text = text.Length > 0 ? text : null,
                Kinds = SelectedKind.Value,
                Sources = SelectedSource.Value,
                Before = _nextBefore,
                Limit = PageSize,
            });
            _loaded.AddRange(page.Items.Where(item => _loaded.TrueForAll(known => known.Id != item.Id)));
            _nextBefore = page.NextBefore;
            HasMore = page.NextBefore is not null;
            Group();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Error = L.T("FileHistory.CouldNotLoad") + exception.Message;
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    private void Group()
    {
        var now = _clock();
        Days.Clear();
        foreach (var day in _loaded.OrderByDescending(static item => item.Id).GroupBy(static item => item.Time.Date))
        {
            Days.Add(new FileHistoryDayViewModel(FileHistoryItemViewModel.DayTitle(day.Key, now), [.. day.Select(item => new FileHistoryItemViewModel(item, RevertAsync))]));
        }
    }
}

using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.Text;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.ViewModels;

/// <summary>
/// Быстрый поиск: строка запроса, список результатов с клавиатурной навигацией, строка состояния.
/// Одна и та же ViewModel стоит за всплывающим окном и за страницей «Поиск» главного окна,
/// поэтому введённый запрос сохраняется между ними.
/// </summary>
/// <remarks>
/// Запрос выполняется с задержкой <see cref="DebounceDelay"/> после последнего ввода; предыдущий запрос отменяется,
/// а устаревший ответ отбрасывается. Пустой запрос показывает подборку по секциям: недавно открытые, закреплённые,
/// недавно изменённые; запись показывается только в первой секции, где встретилась.
/// </remarks>
public sealed partial class QuickSearchViewModel : PageViewModel, IDisposable
{
    private const int NumberedResults = 9;

    private readonly ISearchService _search;
    private readonly IFileActions _actions;
    private readonly Func<DateTimeOffset> _clock;
    private CancellationTokenSource? _pending;
    private IndexStatus? _status;
    private FileDetails? _details;

    /// <summary>
    /// Создаёт ViewModel с макетными действиями над файлами (превью) и сразу загружает подборку (<see cref="PendingRefresh"/>).
    /// </summary>
    public QuickSearchViewModel(ISearchService search)
        : this(search, new SampleFileActions(), static () => DateTimeOffset.Now)
    {
    }

    /// <summary>
    /// Создаёт ViewModel приложения и сразу загружает подборку (<see cref="PendingRefresh"/>).
    /// </summary>
    public QuickSearchViewModel(ISearchService search, IFileActions actions)
        : this(search, actions, static () => DateTimeOffset.Now)
    {
    }

    /// <summary>
    /// Создаёт ViewModel с заданными часами и макетными действиями (для тестов).
    /// </summary>
    public QuickSearchViewModel(ISearchService search, Func<DateTimeOffset> clock)
        : this(search, new SampleFileActions(), clock)
    {
    }

    /// <summary>
    /// Создаёт ViewModel с заданными действиями и часами (для тестов).
    /// </summary>
    public QuickSearchViewModel(ISearchService search, IFileActions actions, Func<DateTimeOffset> clock)
        : base(PageKeys.Search, "Поиск", FASymbol.Find)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(clock);

        _search = search;
        _actions = actions;
        _clock = clock;

        Query = string.Empty;
        StatusText = string.Empty;
        SelectedIndex = -1;
        DebounceDelay = TimeSpan.FromMilliseconds(60);
        PendingRefresh = RefreshAsync();
    }

    /// <summary>Запрошено закрытие окна поиска: <c>Esc</c> при пустом запросе.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Пользователь открыл файл из результатов.</summary>
    public event EventHandler<FileEntry>? FileOpened;

    /// <summary>Пауза между последним вводом и запросом; в тестах можно обнулить.</summary>
    public TimeSpan DebounceDelay { get; set; }

    /// <summary>Результаты текущего запроса или подборка при пустом запросе — только файлы, без заголовков.</summary>
    public ObservableCollection<SearchHit> Results { get; } = [];

    /// <summary>Строки списка: <see cref="SearchHit"/> и заголовки секций <see cref="ResultSectionHeader"/>.</summary>
    public ObservableCollection<object> Rows { get; } = [];

    /// <summary>Последний запущенный запрос; завершается, когда результаты показаны или запрос отменён.</summary>
    public Task PendingRefresh { get; private set; }

    /// <summary>Текст запроса.</summary>
    [ObservableProperty]
    public partial string Query { get; set; }

    /// <summary>Индекс выбранного результата в <see cref="Results"/>; −1, если список пуст.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedResult), nameof(SelectedRow), nameof(PinMenuText), nameof(SelectedDetails))]
    public partial int SelectedIndex { get; set; }

    /// <summary>Строка состояния: количество результатов и время, либо подсказка.</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; }

    /// <summary>Идёт запрос.</summary>
    [ObservableProperty]
    public partial bool IsSearching { get; set; }

    /// <summary>Последний запрос или открытие завершились ошибкой; текст — в <see cref="StatusText"/>.</summary>
    [ObservableProperty]
    public partial bool HasError { get; set; }

    /// <summary>Показать «ничего не найдено».</summary>
    [ObservableProperty]
    public partial bool ShowEmptyState { get; set; }

    /// <summary>Показать список.</summary>
    [ObservableProperty]
    public partial bool ShowResults { get; set; }

    /// <summary>Выбранная строка или <c>null</c>.</summary>
    public SearchHit? SelectedResult => SelectedIndex >= 0 && SelectedIndex < Results.Count ? Results[SelectedIndex] : null;

    /// <summary>Сведения о выбранном файле для панели справа от результатов.</summary>
    public FileDetails? SelectedDetails
    {
        get
        {
            var entry = SelectedResult?.Entry;
            if (!ReferenceEquals(_details?.Entry, entry))
            {
                _details = entry is null ? null : new FileDetails(entry);
            }

            return _details;
        }
    }

    /// <summary>
    /// Выбранная строка списка для привязки <c>SelectedItem</c>: заголовки секций не выбираются, сброс выделения
    /// при перестроении списка игнорируется.
    /// </summary>
    public object? SelectedRow
    {
        get => SelectedResult;
        set
        {
            if (value is SearchHit hit && Results.IndexOf(hit) is >= 0 and var index)
            {
                SelectedIndex = index;
            }
        }
    }

    /// <summary>
    /// Повторяет последний запрос — кнопка «Повторить» при ошибке связи с сервером.
    /// </summary>
    [RelayCommand]
    public Task RetryAsync() => ActivateAsync();

    /// <summary>
    /// Окно поиска показано: подборка или результаты перечитываются — индекс мог измениться, пока окно было скрыто.
    /// </summary>
    public Task ActivateAsync()
    {
        PendingRefresh = RefreshAsync();
        return PendingRefresh;
    }

    /// <summary>
    /// Перезапускает запрос для текущего текста, отменяя предыдущий.
    /// </summary>
    public async Task RefreshAsync()
    {
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _pending, cts);
        if (previous is not null)
        {
            await previous.CancelAsync();
            previous.Dispose();
        }

        var token = cts.Token;
        var raw = Query;
        var query = raw.Trim();
        UpdateFilterChips();

        if (query == HelpQuery)
        {
            ShowHelpPanel();
            return;
        }

        try
        {
            if (query.Length > 0 && DebounceDelay > TimeSpan.Zero)
            {
                await Task.Delay(DebounceDelay, token);
            }

            IsSearching = true;

            // Подсказки идут параллельно поиску: сервер отвечает на них за доли миллисекунды.
            var suggestions = LoadSuggestionsAsync(raw, token);
            if (query.Length == 0)
            {
                var sections = await _search.GetSuggestionsAsync(token);
                var status = await _search.GetIndexStatusAsync(token);
                token.ThrowIfCancellationRequested();
                _status = status;
                ShowSectionResults(sections, status);
            }
            else
            {
                // Пока идёт индексация, состояние обновляется вместе с поиском, чтобы строка показывала ход; потом — нет.
                var statusTask = _status is { IsIndexing: true } ? _search.GetIndexStatusAsync(token) : null;
                var result = await _search.SearchAsync(query, token);
                if (statusTask is not null)
                {
                    _status = await statusTask;
                }

                token.ThrowIfCancellationRequested();
                ShowSearchResult(result);
            }

            var list = await suggestions;
            token.ThrowIfCancellationRequested();
            ShowSuggestionList(raw, list);
        }
        catch (OperationCanceledException)
        {
            // Пользователь продолжил ввод: этот запрос устарел.
        }
        catch (Exception exception) when (!token.IsCancellationRequested)
        {
            // Сервер недоступен или вернул ошибку: окно не падает, следующая буква повторит запрос.
            ShowError("Нет связи с сервером поиска: " + exception.Message);
        }
        finally
        {
            if (ReferenceEquals(_pending, cts))
            {
                IsSearching = false;
            }
        }
    }

    /// <summary>
    /// Сдвигает выделение на <paramref name="delta"/> строк, не выходя за границы списка.
    /// </summary>
    public void MoveSelection(int delta)
    {
        if (Results.Count == 0)
        {
            SelectedIndex = -1;
            return;
        }

        var current = SelectedIndex < 0 ? 0 : SelectedIndex;
        SelectedIndex = Math.Clamp(current + delta, 0, Results.Count - 1);
    }

    /// <summary>
    /// Выбирает строку с номером <paramref name="number"/> (1–9).
    /// </summary>
    /// <returns><c>true</c>, если такая строка есть.</returns>
    public bool SelectNumber(int number)
    {
        var index = Results.ToList().FindIndex(hit => hit.Number == number);
        if (index < 0)
        {
            return false;
        }

        SelectedIndex = index;
        return true;
    }

    /// <summary>
    /// Открывает выбранный файл: отмечает открытие в сервисе и сообщает оболочке через <see cref="FileOpened"/>.
    /// </summary>
    /// <returns><c>true</c>, если строка была выбрана.</returns>
    public async Task<bool> OpenSelectedAsync()
    {
        if (await TryPickFolderAsync())
        {
            return true;
        }

        if (SelectedResult is not { } hit)
        {
            return false;
        }

        try
        {
            var query = Query.Trim();
            await _search.OpenAsync(hit.Entry, query.Length > 0 ? query : null, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ShowError($"Не удалось открыть «{hit.Entry.Name}»: {exception.Message}");
            return false;
        }

        FileOpened?.Invoke(this, hit.Entry);
        return true;
    }

    /// <summary>
    /// <c>Esc</c>: непустой запрос очищается, пустой — закрывает окно.
    /// </summary>
    public void Escape()
    {
        // Выбор папки для диалога отменяется сразу: окно закрывается, диалог остаётся как был.
        if (IsPickingFolder)
        {
            CancelFolderPick();
            Query = string.Empty;
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (Query.Length > 0)
        {
            Query = string.Empty;
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        var pending = Interlocked.Exchange(ref _pending, null);
        pending?.Cancel();
        pending?.Dispose();
    }

    partial void OnQueryChanged(string value)
    {
        OnQueryEdited();
        PendingRefresh = RefreshAsync();
    }

    private void ShowError(string message)
    {
        HasError = true;
        StatusText = message;
        ShowEmptyState = false;
    }

    private void ShowSectionResults(IReadOnlyList<ResultSection> sections, IndexStatus status)
    {
        HasError = false;
        ShowHelp = false;
        UpdateRootChips(status);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var grouped = new List<(string Title, IReadOnlyList<FileEntry> Entries)>();
        foreach (var section in sections)
        {
            var entries = section.Entries.Where(entry => seen.Add(entry.FullPath)).ToArray();
            if (entries.Length > 0)
            {
                grouped.Add((section.Title, entries));
            }
        }

        ReplaceResults(grouped, string.Empty);
        var total = Formatting.Count(status.TotalEntries, "файл", "файла", "файлов") + " в индексе";
        StatusText = status.IsIndexing ? $"{status.DescribeIndexing()} · {total}" : $"{total} · начните вводить имя";
        ShowEmptyState = false;
        ShowResults = Results.Count > 0;
    }

    private void ShowSearchResult(SearchResult result)
    {
        HasError = false;
        ShowHelp = false;
        ReplaceResults([(string.Empty, result.Entries)], Query.Trim());

        if (result.TotalCount == 0)
        {
            StatusText = "Ничего не найдено — попробуйте другое слово или часть имени";
            ShowEmptyState = true;
            ShowResults = false;
            return;
        }

        var elapsed = Math.Max(1, (int)Math.Round(result.Elapsed.TotalMilliseconds));
        var count = Formatting.Count(result.TotalCount, "результат", "результата", "результатов");
        StatusText = (result.IsTotalApproximate ? count.Replace(" ", "+ ", StringComparison.Ordinal) : count) + $" · {elapsed} мс"
            + (_status is { IsIndexing: true } status ? " · " + status.DescribeIndexing() : string.Empty);
        ShowEmptyState = false;
        ShowResults = true;
    }

    private void ReplaceResults(IReadOnlyList<(string Title, IReadOnlyList<FileEntry> Entries)> sections, string highlight)
    {
        var now = _clock();
        Results.Clear();
        Rows.Clear();
        foreach (var (title, entries) in sections)
        {
            if (title.Length > 0)
            {
                Rows.Add(new ResultSectionHeader(title));
            }

            foreach (var entry in entries)
            {
                var hit = new SearchHit(entry, highlight, Results.Count < NumberedResults ? Results.Count + 1 : 0, now);
                Results.Add(hit);
                Rows.Add(hit);
            }
        }

        // Список перестроен: выделение в нём сброшено, даже если индекс прежний — оповестить явно.
        SelectedIndex = Results.Count > 0 ? 0 : -1;
        NotifySelectionChanged();
    }
}

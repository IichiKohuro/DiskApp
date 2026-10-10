using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

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
/// а устаревший ответ отбрасывается. Пустой запрос показывает недавно открытые файлы.
/// </remarks>
public sealed partial class QuickSearchViewModel : PageViewModel, IDisposable
{
    private const int NumberedResults = 9;

    private readonly ISearchService _search;
    private readonly Func<DateTimeOffset> _clock;
    private CancellationTokenSource? _pending;

    /// <summary>
    /// Создаёт ViewModel и сразу загружает недавние файлы (<see cref="PendingRefresh"/>).
    /// </summary>
    public QuickSearchViewModel(ISearchService search)
        : this(search, static () => DateTimeOffset.Now)
    {
    }

    /// <summary>
    /// Создаёт ViewModel с заданными часами (для тестов и превью).
    /// </summary>
    public QuickSearchViewModel(ISearchService search, Func<DateTimeOffset> clock)
        : base(PageKeys.Search, "Поиск", FASymbol.Find)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(clock);

        _search = search;
        _clock = clock;

        Query = string.Empty;
        StatusText = string.Empty;
        SectionHeader = string.Empty;
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

    /// <summary>Результаты текущего запроса или недавние файлы при пустом запросе.</summary>
    public ObservableCollection<SearchHit> Results { get; } = [];

    /// <summary>Последний запущенный запрос; завершается, когда результаты показаны или запрос отменён.</summary>
    public Task PendingRefresh { get; private set; }

    /// <summary>Текст запроса.</summary>
    [ObservableProperty]
    public partial string Query { get; set; }

    /// <summary>Индекс выбранной строки; −1, если список пуст.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedResult))]
    public partial int SelectedIndex { get; set; }

    /// <summary>Строка состояния: количество результатов и время, либо подсказка.</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; }

    /// <summary>Заголовок секции над списком, например «Недавно открытые»; пусто для результатов поиска.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSectionHeader))]
    public partial string SectionHeader { get; set; }

    /// <summary>Идёт запрос.</summary>
    [ObservableProperty]
    public partial bool IsSearching { get; set; }

    /// <summary>Показать «ничего не найдено».</summary>
    [ObservableProperty]
    public partial bool ShowEmptyState { get; set; }

    /// <summary>Показать список.</summary>
    [ObservableProperty]
    public partial bool ShowResults { get; set; }

    /// <summary>Выбранная строка или <c>null</c>.</summary>
    public SearchHit? SelectedResult => SelectedIndex >= 0 && SelectedIndex < Results.Count ? Results[SelectedIndex] : null;

    /// <summary>Есть ли заголовок секции.</summary>
    public bool HasSectionHeader => SectionHeader.Length > 0;

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
        var query = Query.Trim();

        try
        {
            if (query.Length > 0 && DebounceDelay > TimeSpan.Zero)
            {
                await Task.Delay(DebounceDelay, token);
            }

            IsSearching = true;

            if (query.Length == 0)
            {
                var recent = await _search.GetRecentAsync(token);
                var status = await _search.GetIndexStatusAsync(token);
                token.ThrowIfCancellationRequested();
                ShowRecent(recent, status);
            }
            else
            {
                var result = await _search.SearchAsync(query, token);
                token.ThrowIfCancellationRequested();
                ShowSearchResult(result);
            }
        }
        catch (OperationCanceledException)
        {
            // Пользователь продолжил ввод: этот запрос устарел.
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
        if (SelectedResult is not { } hit)
        {
            return false;
        }

        await _search.RecordOpenedAsync(hit.Entry, CancellationToken.None);
        FileOpened?.Invoke(this, hit.Entry);
        return true;
    }

    /// <summary>
    /// <c>Esc</c>: непустой запрос очищается, пустой — закрывает окно.
    /// </summary>
    public void Escape()
    {
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

    partial void OnQueryChanged(string value) => PendingRefresh = RefreshAsync();

    private void ShowRecent(IReadOnlyList<FileEntry> recent, IndexStatus status)
    {
        ReplaceResults(recent, string.Empty);
        SectionHeader = recent.Count > 0 ? "Недавно открытые" : string.Empty;
        StatusText = $"{Formatting.Count(status.TotalEntries, "файл", "файла", "файлов")} в индексе · начните вводить имя";
        ShowEmptyState = false;
        ShowResults = Results.Count > 0;
    }

    private void ShowSearchResult(SearchResult result)
    {
        ReplaceResults(result.Entries, Query.Trim());
        SectionHeader = string.Empty;

        if (result.TotalCount == 0)
        {
            StatusText = "Ничего не найдено — попробуйте другое слово или часть имени";
            ShowEmptyState = true;
            ShowResults = false;
            return;
        }

        var elapsed = Math.Max(1, (int)Math.Round(result.Elapsed.TotalMilliseconds));
        StatusText = $"{Formatting.Count(result.TotalCount, "результат", "результата", "результатов")} · {elapsed} мс";
        ShowEmptyState = false;
        ShowResults = true;
    }

    private void ReplaceResults(IReadOnlyList<FileEntry> entries, string highlight)
    {
        var now = _clock();
        Results.Clear();
        for (var i = 0; i < entries.Count; i++)
        {
            Results.Add(new SearchHit(entries[i], highlight, i < NumberedResults ? i + 1 : 0, now));
        }

        SelectedIndex = Results.Count > 0 ? 0 : -1;
    }
}

using System.Collections.ObjectModel;
using System.Text;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.Text;

using FluentAvalonia.UI.Controls;

using Symbol = FluentIcons.Common.Symbol;

namespace DiskApp.UI.ViewModels;

/// <summary>Что за список на странице «Коллекции».</summary>
public enum CollectionListKind
{
    /// <summary>Избранное (закреплённое).</summary>
    Favorites,

    /// <summary>Ручная коллекция.</summary>
    Manual,

    /// <summary>Сохранённый запрос.</summary>
    SavedQuery,
}

/// <summary>Строка списка коллекций: избранное, ручная коллекция или сохранённый запрос.</summary>
/// <param name="Kind">Вид.</param>
/// <param name="Collection">Коллекция; у избранного — <c>null</c>.</param>
/// <param name="Title">Имя.</param>
/// <param name="Subtitle">Пояснение: вид или строка запроса.</param>
/// <param name="Symbol">Значок.</param>
public sealed record CollectionListItem(CollectionListKind Kind, CollectionInfo? Collection, string Title, string Subtitle, Symbol Symbol)
{
    /// <summary>Записи добавляются и меняют порядок вручную.</summary>
    public bool IsEditable => Kind != CollectionListKind.SavedQuery;

    /// <inheritdoc />
    public override string ToString() => Title;
}

/// <summary>
/// Страница «Коллекции» (PLAN.md, этап 8.7): избранное, ручные коллекции и сохранённые запросы. Записи добавляются
/// перетаскиванием и из файла со списком путей, порядок меняется, список путей выгружается в буфер обмена или файл.
/// Сохранённый запрос перечитывается при входе на страницу и раз в <see cref="AutoRefreshInterval"/>, пока она открыта.
/// </summary>
public sealed partial class CollectionsViewModel : PageViewModel, IPageLifecycle, IDisposable
{
    private readonly ILibraryService _library;
    private readonly ISearchService _search;
    private readonly IFileActions _actions;
    private readonly IDialogService? _dialogs;
    private readonly IInfoBarService? _notices;
    private readonly Func<DateTimeOffset> _clock;
    private CancellationTokenSource? _autoRefresh;
    private int _filesVersion;

    /// <summary>
    /// Создаёт страницу приложения.
    /// </summary>
    public CollectionsViewModel(ILibraryService library, ISearchService search, IFileActions actions, IDialogService dialogs, IInfoBarService notices, ITagEditor? tagEditor = null)
        : this(library, search, actions, dialogs, notices, tagEditor, static () => DateTimeOffset.Now)
    {
    }

    /// <summary>
    /// Создаёт страницу с заданными часами (тесты, превью).
    /// </summary>
    public CollectionsViewModel(ILibraryService library, ISearchService search, IFileActions actions, IDialogService? dialogs, IInfoBarService? notices, ITagEditor? tagEditor, Func<DateTimeOffset> clock)
        : base(PageKeys.Collections, "Коллекции", FASymbol.Library)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(clock);

        _library = library;
        _search = search;
        _actions = actions;
        _dialogs = dialogs;
        _notices = notices;
        TagEditor = tagEditor;
        _clock = clock;
    }

    /// <summary>Как часто перечитывать сохранённый запрос, пока страница открыта.</summary>
    public TimeSpan AutoRefreshInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Диалог тегов файла.</summary>
    public ITagEditor? TagEditor { get; }

    /// <summary>Теги можно менять у файлов.</summary>
    public bool CanEditFileTags => TagEditor is not null;

    /// <summary>Избранное и коллекции.</summary>
    public ObservableCollection<CollectionListItem> Lists { get; } = [];

    /// <summary>Записи выбранного списка.</summary>
    public ObservableCollection<SearchHit> Files { get; } = [];

    /// <summary>Выбранный список.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable), nameof(IsCollection), nameof(IsSavedQuery), nameof(FilesTitle))]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand), nameof(DeleteCommand), nameof(EditQueryCommand), nameof(ImportCommand))]
    public partial CollectionListItem? SelectedList { get; set; }

    /// <summary>Выбранная запись.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand), nameof(MoveDownCommand), nameof(RemoveFileCommand))]
    public partial SearchHit? SelectedFile { get; set; }

    /// <summary>Подпись под списком записей.</summary>
    [ObservableProperty]
    public partial string FilesText { get; set; } = string.Empty;

    /// <summary>Ошибка загрузки; пусто — всё хорошо.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string Error { get; set; } = string.Empty;

    /// <summary>Есть ошибка.</summary>
    public bool HasError => Error.Length > 0;

    /// <summary>Записи выбранного списка добавляются и меняют порядок вручную.</summary>
    public bool IsEditable => SelectedList?.IsEditable == true;

    /// <summary>Выбрана коллекция (её можно переименовать и удалить), а не избранное.</summary>
    public bool IsCollection => SelectedList?.Collection is not null;

    /// <summary>Выбран сохранённый запрос.</summary>
    public bool IsSavedQuery => SelectedList?.Kind == CollectionListKind.SavedQuery;

    /// <summary>Заголовок правой части.</summary>
    public string FilesTitle => SelectedList?.Title ?? "Выберите коллекцию";

    /// <summary>Завершается, когда загружены списки и записи выбранного.</summary>
    public Task PendingLoad { get; private set; } = Task.CompletedTask;

    /// <inheritdoc />
    public void OnNavigatedTo()
    {
        PendingLoad = RefreshAsync();
        StopAutoRefresh();
        _autoRefresh = new CancellationTokenSource();
        _ = AutoRefreshAsync(_autoRefresh.Token);
    }

    /// <inheritdoc />
    public void OnNavigatedFrom() => StopAutoRefresh();

    /// <inheritdoc />
    public void Dispose() => StopAutoRefresh();

    /// <summary>Перечитывает списки и записи выбранного.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        var before = SelectedList;
        try
        {
            var collections = await _library.GetCollectionsAsync();
            Lists.Clear();
            Lists.Add(new CollectionListItem(CollectionListKind.Favorites, null, "Избранное", "Закреплённое: Ctrl+D в любом списке", Symbol.Star));
            foreach (var collection in collections)
            {
                Lists.Add(ToItem(collection));
            }

            SelectedList = Lists.FirstOrDefault(item => SameList(item, before)) ?? Lists[0];
            Error = string.Empty;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Error = "Не удалось загрузить коллекции: " + exception.Message;
        }

        if (Equals(before, SelectedList))
        {
            await LoadFilesAsync();
        }
        else
        {
            await PendingLoad;
        }
    }

    /// <summary>
    /// Добавляет файлы по путям в список: перетаскивание и импорт. Пути вне индекса пропускаются с подсказкой.
    /// </summary>
    /// <param name="paths">Абсолютные пути.</param>
    /// <param name="target">Куда; <c>null</c> — выбранный список.</param>
    /// <returns>Сколько записей добавлено.</returns>
    public async Task<int> AddPathsAsync(IReadOnlyList<string> paths, CollectionListItem? target = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        target ??= SelectedList;
        if (target is null || !target.IsEditable || paths.Count == 0)
        {
            return 0;
        }

        try
        {
            var resolved = await _library.ResolveAsync(paths);
            var added = target.Kind == CollectionListKind.Favorites
                ? await AddToFavoritesAsync(resolved.Entries)
                : await AddToCollectionAsync(target.Collection!, resolved.Entries);

            var message = Formatting.Count(added, "запись добавлена", "записи добавлены", "записей добавлено");
            if (resolved.Missing.Count > 0)
            {
                message += "; " + Formatting.Count(resolved.Missing.Count, "путь", "пути", "путей") + " вне индекса";
            }

            _notices?.Show($"«{target.Title}»", message, resolved.Missing.Count > 0 ? FAInfoBarSeverity.Warning : FAInfoBarSeverity.Success);
            if (SameList(target, SelectedList))
            {
                await LoadFilesAsync();
            }

            return added;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show("Не удалось добавить", exception.Message, FAInfoBarSeverity.Error);
            return 0;
        }
    }

    partial void OnSelectedListChanged(CollectionListItem? value) => PendingLoad = LoadFilesAsync();

    private static bool SameList(CollectionListItem? left, CollectionListItem? right)
        => left is not null && right is not null && left.Kind == right.Kind && left.Collection?.Id == right.Collection?.Id;

    private static CollectionListItem ToItem(CollectionInfo collection) => collection.Type == CollectionType.SavedQuery
        ? new CollectionListItem(CollectionListKind.SavedQuery, collection, collection.Name, collection.Query ?? string.Empty, Symbol.SearchSquare)
        : new CollectionListItem(CollectionListKind.Manual, collection, collection.Name, "Ручная коллекция", Symbol.Collections);

    private async Task LoadFilesAsync()
    {
        var version = ++_filesVersion;
        if (SelectedList is not { } list)
        {
            Files.Clear();
            FilesText = string.Empty;
            return;
        }

        try
        {
            var entries = list.Collection is { } collection
                ? await _library.GetCollectionItemsAsync(collection)
                : await _library.GetFavoritesAsync();
            if (version != _filesVersion)
            {
                return;
            }

            var selected = SelectedFile?.Entry.Id;
            var now = _clock();
            Files.Clear();
            foreach (var entry in entries)
            {
                Files.Add(new SearchHit(entry, string.Empty, 0, now));
            }

            SelectedFile = Files.FirstOrDefault(hit => hit.Entry.Id == selected);
            FilesText = entries.Count > 0
                ? Formatting.Count(entries.Count, "запись", "записи", "записей")
                : list.Kind switch
                {
                    CollectionListKind.Favorites => "Избранного пока нет: Ctrl+D закрепляет файл в поиске и «Обзоре».",
                    CollectionListKind.SavedQuery => "По запросу ничего не найдено.",
                    _ => "Коллекция пуста: перетащите сюда файлы или загрузите список путей.",
                };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Files.Clear();
            FilesText = "Не удалось загрузить записи: " + exception.Message;
        }
    }

    private async Task<int> AddToFavoritesAsync(IReadOnlyList<FileEntry> entries)
    {
        var added = 0;
        foreach (var entry in entries.Where(static entry => !entry.IsFavorite))
        {
            await _actions.SetFavoriteAsync(entry, true);
            added++;
        }

        return added;
    }

    private async Task<int> AddToCollectionAsync(CollectionInfo collection, IReadOnlyList<FileEntry> entries)
    {
        var current = (await _library.GetCollectionItemsAsync(collection)).Select(static entry => entry.Id).ToList();
        var fresh = entries.Select(static entry => entry.Id).Where(id => !current.Contains(id)).ToArray();
        if (fresh.Length > 0)
        {
            await _library.SetCollectionItemsAsync(collection.Id, [.. current, .. fresh]);
        }

        return fresh.Length;
    }

    private async Task AutoRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(AutoRefreshInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (IsSavedQuery)
                {
                    await LoadFilesAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Страницу закрыли.
        }
    }

    private void StopAutoRefresh()
    {
        _autoRefresh?.Cancel();
        _autoRefresh?.Dispose();
        _autoRefresh = null;
    }

    /// <summary>Новая ручная коллекция.</summary>
    [RelayCommand]
    private async Task NewCollectionAsync()
    {
        if (_dialogs is null || await _dialogs.PromptAsync("Новая коллекция", "Имя", string.Empty, "Создать") is not { } name || name.Trim().Length == 0)
        {
            return;
        }

        await CreateAsync(name, CollectionType.Manual, null);
    }

    /// <summary>Новый сохранённый запрос: имя и строка запроса с фильтрами.</summary>
    [RelayCommand]
    private async Task NewSavedQueryAsync()
    {
        if (_dialogs is null
            || await _dialogs.PromptAsync("Новый сохранённый запрос", "Запрос, например: ext:pdf modified:7d", string.Empty, "Далее") is not { } query
            || query.Trim().Length == 0)
        {
            return;
        }

        if (await _dialogs.PromptAsync("Новый сохранённый запрос", "Имя", query.Trim(), "Создать") is not { } name || name.Trim().Length == 0)
        {
            return;
        }

        await CreateAsync(name, CollectionType.SavedQuery, query);
    }

    private async Task CreateAsync(string name, CollectionType type, string? query)
    {
        try
        {
            var created = await _library.CreateCollectionAsync(name, type, query);
            SelectedList = ToItem(created);
            await RefreshAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show("Не удалось создать коллекцию", exception.Message, FAInfoBarSeverity.Error);
        }
    }

    private bool CanChangeCollection() => SelectedList?.Collection is not null;

    /// <summary>Переименовать коллекцию.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeCollection))]
    private async Task RenameAsync()
    {
        if (_dialogs is null || SelectedList?.Collection is not { } collection)
        {
            return;
        }

        if (await _dialogs.PromptAsync("Переименовать коллекцию", "Имя", collection.Name, "Сохранить") is not { } name || name.Trim().Length == 0)
        {
            return;
        }

        await UpdateAsync(collection with { Name = name.Trim() });
    }

    private bool CanEditQuery() => SelectedList?.Kind == CollectionListKind.SavedQuery;

    /// <summary>Изменить строку сохранённого запроса.</summary>
    [RelayCommand(CanExecute = nameof(CanEditQuery))]
    private async Task EditQueryAsync()
    {
        if (_dialogs is null || SelectedList?.Collection is not { } collection)
        {
            return;
        }

        if (await _dialogs.PromptAsync("Изменить запрос", "Запрос", collection.Query ?? string.Empty, "Сохранить") is not { } query || query.Trim().Length == 0)
        {
            return;
        }

        await UpdateAsync(collection with { Query = query.Trim() });
    }

    private async Task UpdateAsync(CollectionInfo collection)
    {
        try
        {
            var updated = await _library.UpdateCollectionAsync(collection);
            SelectedList = ToItem(updated);
            await RefreshAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show("Не удалось изменить коллекцию", exception.Message, FAInfoBarSeverity.Error);
        }
    }

    /// <summary>Удалить коллекцию; файлы остаются на месте.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeCollection))]
    private async Task DeleteAsync()
    {
        if (_dialogs is null || SelectedList?.Collection is not { } collection)
        {
            return;
        }

        if (!await _dialogs.ConfirmAsync("Удалить коллекцию", $"Удалить «{collection.Name}»? Файлы останутся на месте.", "Удалить"))
        {
            return;
        }

        try
        {
            await _library.DeleteCollectionAsync(collection.Id);
            SelectedList = null;
            await RefreshAsync();
            _notices?.Show("Коллекция удалена", $"«{collection.Name}»", FAInfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show("Не удалось удалить коллекцию", exception.Message, FAInfoBarSeverity.Error);
        }
    }

    /// <summary>Открыть запись.</summary>
    [RelayCommand]
    private async Task OpenFileAsync(SearchHit? hit)
    {
        if ((hit ?? SelectedFile) is not { } target)
        {
            return;
        }

        try
        {
            await _search.OpenAsync(target.Entry, null, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show("Не удалось открыть", exception.Message, FAInfoBarSeverity.Error);
        }
    }

    /// <summary>Показать запись в Проводнике.</summary>
    [RelayCommand]
    private async Task RevealFileAsync()
    {
        if (SelectedFile is not { } hit)
        {
            return;
        }

        try
        {
            await _actions.RevealAsync(hit.Entry);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show("Не удалось показать в папке", exception.Message, FAInfoBarSeverity.Error);
        }
    }

    /// <summary>Теги выбранной записи.</summary>
    [RelayCommand]
    private async Task EditFileTagsAsync()
    {
        if (SelectedFile is not { } hit || TagEditor is null)
        {
            return;
        }

        try
        {
            if (await TagEditor.EditAsync([hit.Entry]) is { Count: > 0 })
            {
                await LoadFilesAsync();
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show("Не удалось изменить теги", exception.Message, FAInfoBarSeverity.Error);
        }
    }

    private bool CanChangeFile() => SelectedFile is not null && IsEditable;

    /// <summary>Убрать запись из списка: открепить или исключить из коллекции. Файл остаётся на месте.</summary>
    [RelayCommand(CanExecute = nameof(CanChangeFile))]
    private async Task RemoveFileAsync()
    {
        if (SelectedFile is not { } hit || SelectedList is not { } list)
        {
            return;
        }

        try
        {
            if (list.Kind == CollectionListKind.Favorites)
            {
                await _actions.SetFavoriteAsync(hit.Entry, false);
            }
            else
            {
                await _library.SetCollectionItemsAsync(list.Collection!.Id, [.. Files.Where(file => file != hit).Select(static file => file.Entry.Id)]);
            }

            await LoadFilesAsync();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show("Не удалось убрать запись", exception.Message, FAInfoBarSeverity.Error);
        }
    }

    /// <summary>Выше в списке (<c>Alt+↑</c>).</summary>
    [RelayCommand(CanExecute = nameof(CanChangeFile))]
    private Task MoveUpAsync() => MoveAsync(-1);

    /// <summary>Ниже в списке (<c>Alt+↓</c>).</summary>
    [RelayCommand(CanExecute = nameof(CanChangeFile))]
    private Task MoveDownAsync() => MoveAsync(1);

    private async Task MoveAsync(int delta)
    {
        if (SelectedFile is not { } hit || SelectedList is not { } list)
        {
            return;
        }

        var index = Files.IndexOf(hit);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Files.Count)
        {
            return;
        }

        Files.Move(index, target);
        SelectedFile = hit;
        var order = Files.Select(static file => file.Entry.Id).ToArray();
        try
        {
            if (list.Kind == CollectionListKind.Favorites)
            {
                await _library.ReorderFavoritesAsync(order);
            }
            else
            {
                await _library.SetCollectionItemsAsync(list.Collection!.Id, order);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show("Не удалось изменить порядок", exception.Message, FAInfoBarSeverity.Error);
            await LoadFilesAsync();
        }
    }

    /// <summary>Пути всех записей в буфер обмена, по одному в строке.</summary>
    [RelayCommand]
    private async Task CopyPathsAsync()
    {
        if (Files.Count == 0)
        {
            return;
        }

        try
        {
            await _actions.CopyPathsAsync([.. Files.Select(static file => file.Entry)]);
            _notices?.Show("Пути скопированы", Formatting.Count(Files.Count, "путь", "пути", "путей"), FAInfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show("Не удалось скопировать пути", exception.Message, FAInfoBarSeverity.Error);
        }
    }

    /// <summary>Сохранить список путей в текстовый файл (UTF-8, путь в строке).</summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        if (_dialogs is null || SelectedList is not { } list || Files.Count == 0)
        {
            return;
        }

        if (await _dialogs.PickSaveFileAsync("Сохранить список путей", PathText.ValidateFileName(list.Title) is null ? list.Title + ".txt" : "Коллекция.txt") is not { } path)
        {
            return;
        }

        try
        {
            await File.WriteAllLinesAsync(path, Files.Select(static file => file.Entry.FullPath), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _notices?.Show("Список сохранён", path, FAInfoBarSeverity.Success);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _notices?.Show("Не удалось сохранить список", exception.Message, FAInfoBarSeverity.Error);
        }
    }

    private bool CanImport() => SelectedList?.IsEditable == true;

    /// <summary>
    /// Добавить записи из текстового файла: путь в строке, пустые строки и строки с <c>#</c> пропускаются.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        if (_dialogs is null || await _dialogs.PickOpenFileAsync("Загрузить список путей") is not { } path)
        {
            return;
        }

        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _notices?.Show("Не удалось прочитать список", exception.Message, FAInfoBarSeverity.Error);
            return;
        }

        await AddPathsAsync(ParsePaths(lines));
    }

    /// <summary>Пути из строк списка: без пустых строк, комментариев <c>#</c> и кавычек вокруг пути.</summary>
    public static IReadOnlyList<string> ParsePaths(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        return [.. lines
            .Select(static line => line.Trim().Trim('"').Trim())
            .Where(static line => line.Length > 0 && !line.StartsWith('#'))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}

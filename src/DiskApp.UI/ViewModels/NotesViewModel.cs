using System.Collections.ObjectModel;

using Avalonia.Controls;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using DiskApp.UI.Controls;
using DiskApp.UI.Localization;
using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.Text;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.ViewModels;

/// <summary>Строка списка заметок.</summary>
public sealed partial class NoteItem : ObservableObject
{
    /// <summary>Создаёт строку.</summary>
    public NoteItem(NoteInfo note, Func<DateTimeOffset> clock)
    {
        Note = note ?? throw new ArgumentNullException(nameof(note));
        Clock = clock;
    }

    /// <summary>Заметка.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Snippet), nameof(ModifiedText), nameof(TagLabels), nameof(HasTags), nameof(IsPinned))]
    public partial NoteInfo Note { get; set; }

    /// <summary>Заголовок; у пустой заметки — «Без названия».</summary>
    public string Title => Note.Title.Length > 0 ? Note.Title : L.T("Notes.Untitled");

    /// <summary>Начало текста после заголовка, без разметки.</summary>
    public string Snippet => NotesViewModel.SnippetOf(Note.Body);

    /// <summary>Когда изменена.</summary>
    public string ModifiedText => Formatting.RelativeTime(Note.Modified, Clock());

    /// <summary>Закреплена.</summary>
    public bool IsPinned => Note.IsPinned;

    /// <summary>Теги заметки — чипы.</summary>
    public IReadOnlyList<TagLabel> TagLabels => [.. Note.Tags.Select(static tag => new TagLabel(tag.Name, tag.Color))];

    /// <summary>Есть теги.</summary>
    public bool HasTags => Note.Tags.Count > 0;

    private Func<DateTimeOffset> Clock { get; }

    /// <inheritdoc />
    public override string ToString() => Title;
}

/// <summary>Фильтр списка по тегу заметок; без тега — все.</summary>
/// <param name="Tag">Тег; <c>null</c> — «Все теги».</param>
/// <param name="Text">Подпись.</param>
public sealed record NoteTagFilter(TagInfo? Tag, string Text)
{
    /// <inheritdoc />
    public override string ToString() => Text;
}

/// <summary>
/// Страница «Заметки» (PLAN.md, этап 23, ADR-0014): список слева — закреплённые сверху, поиск по тексту, фильтр по тегу,
/// корзина; справа — заметка: текст в Markdown, правка или просмотр, свои теги, закрепить, удалить с «Отменить».
/// Изменения сохраняются сами после паузы в наборе; новая заметка попадает на сервер, когда в ней что-то написано.
/// </summary>
public sealed partial class NotesViewModel : PageViewModel, IPageLifecycle
{
    /// <summary>Ширина списка заметок по умолчанию.</summary>
    public const double DefaultListWidth = 320;

    /// <summary>Уже нельзя: заголовки не читаются.</summary>
    public const double MinListWidth = 220;

    /// <summary>Шире нельзя: самой заметке нужно место.</summary>
    public const double MaxListWidth = 700;

    private const string ListLayoutKey = "notes.list";

    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(250);

    // Теги сохраняются почти сразу, но не внутри уведомления коллекции: чип можно подкрасить, а несколько крестиков подряд — одно сохранение.
    private static readonly TimeSpan TagsSaveDelay = TimeSpan.FromMilliseconds(100);

    private readonly INotesService _notes;
    private readonly IDialogService? _dialogs;
    private readonly IInfoBarService? _notices;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILayoutStore? _layout;
    private IReadOnlyList<TagInfo> _knownTags = [];
    private CancellationTokenSource? _saveDelay;
    private CancellationTokenSource? _searchDelay;
    private Task _saving = Task.CompletedTask;
    private bool _loadingNote;
    private bool _loadingFilters;

    /// <summary>Создаёт страницу.</summary>
    /// <param name="notes">Заметки.</param>
    /// <param name="dialogs">Диалоги: удаление навсегда, правка тега.</param>
    /// <param name="notices">Уведомления: «Отменить» после удаления.</param>
    /// <param name="clock">Часы.</param>
    /// <param name="layout">Ширина списка между запусками; <c>null</c> — не запоминается.</param>
    public NotesViewModel(INotesService notes, IDialogService? dialogs = null, IInfoBarService? notices = null, Func<DateTimeOffset>? clock = null, ILayoutStore? layout = null)
        : base(PageKeys.Notes, "Notes.Title", FASymbol.Edit)
    {
        _notes = notes ?? throw new ArgumentNullException(nameof(notes));
        _dialogs = dialogs;
        _notices = notices;
        _clock = clock ?? (static () => DateTimeOffset.Now);
        ListColumnWidth = new GridLength(Math.Clamp(layout?.GetWidth(ListLayoutKey) ?? DefaultListWidth, MinListWidth, MaxListWidth));
        _layout = layout;
        TagFilters.Add(new NoteTagFilter(null, L.T("Notes.AllTags")));
        SelectedTagFilter = TagFilters[0];
    }

    /// <summary>Новая заметка создана: поле текста получает фокус, чтобы сразу писать.</summary>
    public event EventHandler? EditorFocusRequested;

    /// <summary>Заметки списка.</summary>
    public ObservableCollection<NoteItem> Notes { get; } = [];

    /// <summary>Ширина списка: тянется разделителем и запоминается между запусками.</summary>
    [ObservableProperty]
    public partial GridLength ListColumnWidth { get; set; }

    /// <summary>Фильтры по тегу: «Все теги» и теги заметок.</summary>
    public ObservableCollection<NoteTagFilter> TagFilters { get; } = [];

    /// <summary>Выбранный фильтр.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTagFilter))]
    public partial NoteTagFilter? SelectedTagFilter { get; set; }

    /// <summary>Выбран тег, а не «Все теги»: его можно переименовать или удалить.</summary>
    public bool HasTagFilter => SelectedTagFilter?.Tag is not null;

    /// <summary>Поиск по тексту заметок.</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>Показана корзина заметок.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit), nameof(EmptyText), nameof(IsEditing))]
    public partial bool ShowTrash { get; set; }

    /// <summary>Выбранная строка.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote), nameof(CanEdit), nameof(IsEditing))]
    public partial NoteItem? SelectedNote { get; set; }

    /// <summary>Текст открытой заметки.</summary>
    [ObservableProperty]
    public partial string Body { get; set; } = string.Empty;

    /// <summary>Открытая заметка закреплена.</summary>
    [ObservableProperty]
    public partial bool IsPinned { get; set; }

    /// <summary>Просмотр вместо правки.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(PreviewToggleText))]
    public partial bool IsPreview { get; set; }

    /// <summary>Теги открытой заметки.</summary>
    [ObservableProperty]
    public partial AutoTagsEditorViewModel? TagsEditor { get; set; }

    /// <summary>Заметка открыта.</summary>
    public bool HasNote => SelectedNote is not null;

    /// <summary>Открытую заметку можно менять: она не в корзине.</summary>
    public bool CanEdit => HasNote && !ShowTrash;

    /// <summary>Видно поле правки.</summary>
    public bool IsEditing => CanEdit && !IsPreview;

    /// <summary>Подпись переключателя: «Просмотр» или «Правка».</summary>
    public string PreviewToggleText => IsPreview ? L.T("Notes.Edit") : L.T("Notes.Preview");

    /// <summary>Подпись вместо пустого списка.</summary>
    public string EmptyText => ShowTrash ? L.T("Notes.TrashEmpty") : SearchText.Trim().Length > 0 || HasTagFilter ? L.T("Notes.NothingFound") : L.T("Notes.NoNotesYet");

    /// <summary>Список пуст.</summary>
    public bool IsEmpty => Notes.Count == 0;

    /// <summary>Завершается, когда закончилось последнее чтение списка (тесты).</summary>
    public Task PendingLoad { get; private set; } = Task.CompletedTask;

    /// <summary>Начало текста после заголовка без разметки — для строки списка.</summary>
    public static string SnippetOf(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var lines = body.Split('\n')
            .Select(static line => line.Trim().TrimStart('#', '>', ' ').Replace("- [ ]", string.Empty, StringComparison.Ordinal).Replace("- [x]", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("**", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal).TrimStart('-', '*', ' '))
            .Where(static line => line.Length > 0)
            .Skip(1);
        var text = string.Join(" · ", lines);
        return text.Length <= 140 ? text : text[..139] + "…";
    }

    /// <inheritdoc />
    public void OnNavigatedTo() => PendingLoad = ReloadAsync(SelectedNote?.Note.Id);

    /// <inheritdoc />
    public void OnNavigatedFrom() => _ = FlushAsync();

    /// <summary>Сохраняет отложенную правку сразу: уход со страницы, смена заметки, тесты.</summary>
    public Task FlushAsync()
    {
        Interlocked.Exchange(ref _saveDelay, null)?.Cancel();
        return SaveQueuedAsync(Capture(SelectedNote));
    }

    /// <summary>Флажок отмечен в просмотре: меняется текст заметки и сохраняется.</summary>
    public void ToggleTask(MarkdownTaskToggle toggle)
    {
        ArgumentNullException.ThrowIfNull(toggle);
        if (CanEdit)
        {
            Body = MarkdownView.ToggleTask(Body, toggle);
        }
    }

    /// <inheritdoc />
    protected override Task RelocalizeAsync()
    {
        OnPropertyChanged(nameof(PreviewToggleText));
        OnPropertyChanged(nameof(EmptyText));
        return ReloadAsync(SelectedNote?.Note.Id);
    }

    /// <summary>Новая заметка: сразу в правке, на сервер попадёт с первым словом.</summary>
    [RelayCommand]
    private async Task NewNoteAsync()
    {
        await FlushAsync();
        if (ShowTrash)
        {
            ShowTrash = false;
            await ReloadAsync(null);
        }

        var draft = new NoteItem(new NoteInfo(0, string.Empty, string.Empty, false, _clock(), null, []), _clock);
        Notes.Insert(0, draft);
        OnPropertyChanged(nameof(IsEmpty));
        IsPreview = false;
        SelectedNote = draft;
        EditorFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Закрепить или открепить открытую заметку.</summary>
    [RelayCommand]
    private async Task TogglePinAsync()
    {
        if (!CanEdit)
        {
            return;
        }

        IsPinned = !IsPinned;
        if (SelectedNote?.Note.Id == 0)
        {
            // Черновик ещё не на сервере: закрепление сохранится вместе с первым словом.
            return;
        }

        await FlushAsync();
        await ReloadAsync(SelectedNote?.Note.Id);
    }

    /// <summary>Переключить правку и просмотр.</summary>
    [RelayCommand]
    private void TogglePreview() => IsPreview = !IsPreview;

    /// <summary>Удалить открытую заметку в корзину — с «Отменить».</summary>
    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedNote is not { } item || !CanEdit)
        {
            return;
        }

        await FlushAsync();
        var note = item.Note;
        if (note.Id == 0)
        {
            Notes.Remove(item);
            SelectedNote = Notes.FirstOrDefault();
            OnPropertyChanged(nameof(IsEmpty));
            return;
        }

        if (!await TryAsync(() => _notes.DeleteAsync(note.Id)))
        {
            return;
        }

        await ReloadAsync(null);
        _notices?.Show(L.T("Notes.Deleted"), item.Title, FAInfoBarSeverity.Informational, L.T("Notes.Undo"), () => _ = RestoreByIdAsync(note.Id));
    }

    /// <summary>Вернуть заметку из корзины.</summary>
    [RelayCommand]
    private Task RestoreAsync() => SelectedNote is { } item && ShowTrash ? RestoreByIdAsync(item.Note.Id) : Task.CompletedTask;

    /// <summary>Удалить заметку из корзины навсегда — после подтверждения.</summary>
    [RelayCommand]
    private async Task DeleteForeverAsync()
    {
        if (SelectedNote is not { } item || !ShowTrash || _dialogs is null)
        {
            return;
        }

        if (await _dialogs.ConfirmAsync(L.T("Notes.DeleteForeverTitle"), L.F("Notes.DeleteForeverMessage", item.Title), L.T("Notes.DeleteForever"))
            && await TryAsync(() => _notes.DeleteAsync(item.Note.Id, permanent: true)))
        {
            await ReloadAsync(null);
        }
    }

    /// <summary>Показать корзину заметок или вернуться к заметкам.</summary>
    [RelayCommand]
    private async Task ToggleTrashAsync()
    {
        await FlushAsync();
        ShowTrash = !ShowTrash;
        await ReloadAsync(null);
    }

    /// <summary>Переименовать или перекрасить выбранный в фильтре тег заметок.</summary>
    [RelayCommand]
    private async Task EditTagAsync()
    {
        if (SelectedTagFilter?.Tag is not { } tag || _dialogs is null)
        {
            return;
        }

        if (await _dialogs.EditTagAsync(L.T("Notes.EditTagTitle"), tag.Name, tag.Color, L.T("Common.Save")) is { } input
            && await TryAsync(() => _notes.UpdateTagAsync(tag.Id, input.Name, input.Color)))
        {
            await ReloadAsync(SelectedNote?.Note.Id, keepTagId: tag.Id);
        }
    }

    /// <summary>Удалить выбранный в фильтре тег у всех заметок — после подтверждения.</summary>
    [RelayCommand]
    private async Task DeleteTagAsync()
    {
        if (SelectedTagFilter?.Tag is not { } tag || _dialogs is null)
        {
            return;
        }

        if (await _dialogs.ConfirmAsync(L.T("Notes.DeleteTagTitle"), L.F("Notes.DeleteTagMessage", tag.Name), L.T("Notes.DeleteTag"))
            && await TryAsync(() => _notes.DeleteTagAsync(tag.Id)))
        {
            await ReloadAsync(SelectedNote?.Note.Id);
        }
    }

    partial void OnSelectedNoteChanged(NoteItem? oldValue, NoteItem? newValue)
    {
        // Правка прежней заметки сохраняется снимком, снятым до того, как поле покажет новую: текст в поле ещё её.
        if (oldValue is not null && !ReferenceEquals(oldValue, newValue))
        {
            Interlocked.Exchange(ref _saveDelay, null)?.Cancel();
            var pending = Capture(oldValue);
            _ = SaveQueuedAsync(pending);
            if (oldValue.Note.Id == 0 && pending is { Body.Length: 0, Names.Count: 0 } && Notes.Contains(oldValue))
            {
                // Пустой черновик никуда не сохраняется — просто исчезает.
                Notes.Remove(oldValue);
                OnPropertyChanged(nameof(IsEmpty));
            }
        }

        _loadingNote = true;
        try
        {
            Body = newValue?.Note.Body ?? string.Empty;
            IsPinned = newValue?.Note.IsPinned ?? false;
            if (TagsEditor is not null)
            {
                TagsEditor.Tags.CollectionChanged -= OnTagsChanged;
            }

            TagsEditor = newValue is null
                ? null
                : new AutoTagsEditorViewModel(newValue.Note.Tags, _knownTags) { Watermark = L.T("Notes.TagWatermark"), Hint = string.Empty };
            if (TagsEditor is not null)
            {
                TagsEditor.Tags.CollectionChanged += OnTagsChanged;
            }
        }
        finally
        {
            _loadingNote = false;
        }
    }

    partial void OnBodyChanged(string value) => QueueSave(SaveDelay);

    partial void OnListColumnWidthChanged(GridLength value)
    {
        if (value.IsAbsolute)
        {
            _layout?.SetWidth(ListLayoutKey, value.Value);
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(EmptyText));
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _searchDelay, cts)?.Cancel();
        _ = ReloadAfterAsync(cts.Token);
    }

    partial void OnSelectedTagFilterChanged(NoteTagFilter? value)
    {
        OnPropertyChanged(nameof(EmptyText));
        if (!_loadingFilters)
        {
            PendingLoad = ReloadAsync(SelectedNote?.Note.Id);
        }
    }

    private void OnTagsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => QueueSave(TagsSaveDelay);

    private void QueueSave(TimeSpan delay)
    {
        if (_loadingNote || !CanEdit)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _saveDelay, cts)?.Cancel();
        _ = SaveAfterAsync(delay, cts.Token);
    }

    private async Task SaveAfterAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await SaveQueuedAsync(Capture(SelectedNote));
    }

    // Что сохранить: заметка и состояние поля на момент снимка — после смены заметки поле уже показывает другую.
    private SaveRequest? Capture(NoteItem? item)
        => item is null || ShowTrash
            ? null
            : new SaveRequest(item, Body, IsPinned, TagsEditor?.Names ?? [.. item.Note.Tags.Select(static tag => tag.Name)], TagsEditor?.NewNames ?? []);

    // Сохранения идут по одному: следующее ждёт предыдущее, иначе быстрый набор мог бы обогнать сам себя.
    private Task SaveQueuedAsync(SaveRequest? request)
    {
        var previous = _saving;
        var next = SaveAfterPreviousAsync(previous, request);
        _saving = next;
        return next;
    }

    private async Task SaveAfterPreviousAsync(Task previous, SaveRequest? request)
    {
        await previous;
        if (request is null)
        {
            return;
        }

        var note = request.Item.Note;
        var sameTags = request.Names.Count == note.Tags.Count && request.Names.All(name => note.Tags.Any(tag => string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase)));
        if ((note.Body == request.Body && note.IsPinned == request.IsPinned && sameTags) || (note.Id == 0 && request.Body.Trim().Length == 0 && request.Names.Count == 0))
        {
            return;
        }

        await TryAsync(async () =>
        {
            var created = request.NewNames.Where(name => !_knownTags.Any(tag => string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (created.Length > 0)
            {
                var colors = _knownTags.Select(static tag => tag.Color).ToList();
                foreach (var name in created)
                {
                    var color = TagPalette.Next(colors);
                    await _notes.CreateTagAsync(name, color);
                    colors.Add(color);
                }

                _knownTags = await _notes.GetTagsAsync();
                RebuildFilters();
            }

            var saved = note.Id == 0
                ? await _notes.CreateAsync(request.Body, request.Names)
                : await _notes.UpdateAsync(note.Id, request.Body, request.IsPinned, request.Names);
            if (note.Id == 0 && request.IsPinned)
            {
                saved = await _notes.UpdateAsync(saved.Id, request.Body, request.IsPinned, request.Names);
            }

            request.Item.Note = saved;
            if (ReferenceEquals(SelectedNote, request.Item) && TagsEditor is { } editor)
            {
                // Новый тег получил цвет при сохранении — чип в заметке тоже.
                _loadingNote = true;
                try
                {
                    editor.UpdateColors(saved.Tags);
                }
                finally
                {
                    _loadingNote = false;
                }
            }
        });
    }

    private async Task ReloadAfterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SearchDelay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        PendingLoad = ReloadAsync(SelectedNote?.Note.Id);
        await PendingLoad;
    }

    private async Task ReloadAsync(long? selectId, long? keepTagId = null)
    {
        await FlushAsync();
        if (selectId == 0)
        {
            // Черновик мог только что сохраниться и получить номер — выделение остаётся на нём.
            selectId = SelectedNote?.Note.Id;
        }

        IReadOnlyList<NoteInfo> list = [];
        await TryAsync(async () =>
        {
            _knownTags = await _notes.GetTagsAsync();
            RebuildFilters(keepTagId);
            list = await _notes.GetNotesAsync(SearchText.Trim(), SelectedTagFilter?.Tag?.Id, ShowTrash);
        });

        Notes.Clear();
        foreach (var note in list)
        {
            Notes.Add(new NoteItem(note, _clock));
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
        SelectedNote = Notes.FirstOrDefault(item => item.Note.Id == selectId && selectId is not 0) ?? Notes.FirstOrDefault();
    }

    private void RebuildFilters(long? keepTagId = null)
    {
        var selected = keepTagId ?? SelectedTagFilter?.Tag?.Id;
        _loadingFilters = true;
        try
        {
            TagFilters.Clear();
            TagFilters.Add(new NoteTagFilter(null, L.T("Notes.AllTags")));
            foreach (var tag in _knownTags)
            {
                TagFilters.Add(new NoteTagFilter(tag, tag.Name));
            }

            SelectedTagFilter = TagFilters.FirstOrDefault(filter => filter.Tag?.Id == selected) ?? TagFilters[0];
        }
        finally
        {
            _loadingFilters = false;
        }
    }

    private async Task RestoreByIdAsync(long id)
    {
        if (await TryAsync(() => _notes.RestoreAsync(id)))
        {
            ShowTrash = false;
            await ReloadAsync(id);
        }
    }

    private async Task<bool> TryAsync(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _notices?.Show(L.T("Notes.Title"), L.F("Notes.Failed", exception.Message), FAInfoBarSeverity.Error);
            return false;
        }
    }

    private sealed record SaveRequest(NoteItem Item, string Body, bool IsPinned, IReadOnlyList<string> Names, IReadOnlyList<string> NewNames);
}

using DiskApp.UI.Controls;
using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Страница «Заметки» (PLAN.md, этап 23): пустой черновик не сохраняется, набранное сохраняется само и в свою заметку
/// даже после быстрой смены выделения, закреплённые сверху, фильтр и поиск, свои теги, удаление с «Отменить», корзина.
/// </summary>
public sealed class NotesViewModelTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 9, 0, 0, TimeSpan.FromHours(3));

    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly IInfoBarService _notices = Substitute.For<IInfoBarService>();
    private readonly SampleNotesService _service;
    private DateTimeOffset _now = Start;

    public NotesViewModelTests()
    {
        // Часы идут вперёд с каждым вызовом: порядок «новые сверху» не зависит от скорости теста.
        _service = new SampleNotesService(Tick);
    }

    [Fact]
    public async Task NewNote_LeftEmpty_IsNotSavedAndDisappears()
    {
        // Arrange
        await _service.CreateAsync("Первая", [], TestContext.Current.CancellationToken);
        var page = await OpenAsync();

        // Act
        await page.NewNoteCommand.ExecuteAsync(null);
        var withDraft = page.Notes.Count;
        page.SelectedNote = page.Notes[1];
        await page.FlushAsync();

        // Assert
        withDraft.ShouldBe(2);
        page.Notes.ShouldHaveSingleItem().Title.ShouldBe("Первая");
        (await _service.GetNotesAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task NewNote_Typed_SavedWithTitleFromFirstLine()
    {
        // Arrange
        var page = await OpenAsync();
        var focusRequests = 0;
        page.EditorFocusRequested += (_, _) => focusRequests++;
        await page.NewNoteCommand.ExecuteAsync(null);

        // Act
        page.Body = "# Покупки\n\n- [ ] молоко";
        await page.FlushAsync();

        // Assert
        focusRequests.ShouldBe(1, "у новой заметки курсор сразу в поле текста");
        var saved = (await _service.GetNotesAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldHaveSingleItem();
        saved.Title.ShouldBe("Покупки");
        page.SelectedNote.ShouldNotBeNull().Note.Id.ShouldBe(saved.Id);
        page.SelectedNote.Title.ShouldBe("Покупки");
        page.SelectedNote.Snippet.ShouldBe("молоко");
    }

    [Fact]
    public async Task Typing_SavedByItselfAfterPause()
    {
        // Arrange
        var note = await _service.CreateAsync("Черновик", [], TestContext.Current.CancellationToken);
        var page = await OpenAsync();

        // Act
        page.Body = "Черновик, дописанный";
        var saved = await WaitAsync(async () => (await _service.GetNotesAsync(cancellationToken: TestContext.Current.CancellationToken)).Single().Body == "Черновик, дописанный");

        // Assert
        saved.ShouldBeTrue();
        page.SelectedNote.ShouldNotBeNull().Note.Id.ShouldBe(note.Id);
    }

    [Fact]
    public async Task EditThenSwitchAtOnce_SavesTextIntoNoteItWasTypedIn()
    {
        // Arrange
        var first = await _service.CreateAsync("Первая", [], TestContext.Current.CancellationToken);
        var second = await _service.CreateAsync("Вторая", [], TestContext.Current.CancellationToken);
        var page = await OpenAsync();
        page.SelectedNote = page.Notes.Single(item => item.Note.Id == first.Id);

        // Act: правка и сразу щелчок по другой заметке — раньше, чем сработает пауза автосохранения
        page.Body = "Первая, исправленная";
        page.SelectedNote = page.Notes.Single(item => item.Note.Id == second.Id);
        await page.FlushAsync();

        // Assert
        var notes = await _service.GetNotesAsync(cancellationToken: TestContext.Current.CancellationToken);
        notes.Single(note => note.Id == first.Id).Body.ShouldBe("Первая, исправленная");
        notes.Single(note => note.Id == second.Id).Body.ShouldBe("Вторая");
        page.Body.ShouldBe("Вторая");
    }

    [Fact]
    public async Task TogglePin_MovesNoteToTop()
    {
        // Arrange
        var old = await _service.CreateAsync("Старая", [], TestContext.Current.CancellationToken);
        await _service.CreateAsync("Новая", [], TestContext.Current.CancellationToken);
        var page = await OpenAsync();
        page.SelectedNote = page.Notes.Single(item => item.Note.Id == old.Id);

        // Act
        await page.TogglePinCommand.ExecuteAsync(null);

        // Assert
        page.Notes.Select(static item => item.Title).ShouldBe(["Старая", "Новая"]);
        page.Notes[0].IsPinned.ShouldBeTrue();
        page.SelectedNote.ShouldBe(page.Notes[0]);
        page.IsPinned.ShouldBeTrue();
    }

    [Fact]
    public async Task TogglePin_OnEmptyDraft_KeepsDraftAndPinsOnFirstWord()
    {
        // Arrange
        var page = await OpenAsync();
        await page.NewNoteCommand.ExecuteAsync(null);

        // Act
        await page.TogglePinCommand.ExecuteAsync(null);
        var draftKept = page.SelectedNote?.Note.Id == 0;
        page.Body = "Важное";
        await page.FlushAsync();

        // Assert
        draftKept.ShouldBeTrue();
        (await _service.GetNotesAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldHaveSingleItem().IsPinned.ShouldBeTrue();
    }

    [Fact]
    public async Task TagFilter_ShowsOnlyNotesWithTag()
    {
        // Arrange
        await _service.CreateAsync("Дача", ["дом"], TestContext.Current.CancellationToken);
        await _service.CreateAsync("Отчёт", ["работа"], TestContext.Current.CancellationToken);
        var page = await OpenAsync();

        // Act
        page.SelectedTagFilter = page.TagFilters.Single(static filter => filter.Text == "дом");
        await page.PendingLoad;

        // Assert
        page.TagFilters.Select(static filter => filter.Text).ShouldBe(["Все теги", "дом", "работа"]);
        page.HasTagFilter.ShouldBeTrue();
        page.Notes.ShouldHaveSingleItem().Title.ShouldBe("Дача");
    }

    [Fact]
    public async Task Search_FindsByTextAfterPause_EmptyResultExplains()
    {
        // Arrange
        await _service.CreateAsync("Ёлка\nукрасить к празднику", [], TestContext.Current.CancellationToken);
        await _service.CreateAsync("Отчёт", [], TestContext.Current.CancellationToken);
        var page = await OpenAsync();

        // Act
        page.SearchText = "ЕЛКА";
        var found = await WaitAsync(() => Task.FromResult(page.Notes.Count == 1));
        page.SearchText = "нет такого";
        var empty = await WaitAsync(() => Task.FromResult(page.IsEmpty));

        // Assert
        found.ShouldBeTrue();
        empty.ShouldBeTrue();
        page.EmptyText.ShouldBe("Ничего не найдено.");
    }

    [Fact]
    public async Task TagsEditor_NewTag_SavedWithNoteAsOwnTagWithColor()
    {
        // Arrange
        await _service.CreateAsync("Идея", [], TestContext.Current.CancellationToken);
        var page = await OpenAsync();

        // Act
        page.TagsEditor.ShouldNotBeNull().AddCommand.Execute("мысли");
        await page.FlushAsync();

        // Assert
        var note = (await _service.GetNotesAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldHaveSingleItem();
        var tag = note.Tags.ShouldHaveSingleItem();
        tag.Name.ShouldBe("мысли");
        tag.Color.ShouldNotBeNullOrEmpty();
        page.TagsEditor.Tags.ShouldHaveSingleItem().Color.ShouldBe(tag.Color, "чип в заметке подкрашен цветом сохранённого тега");
        page.TagFilters.Select(static filter => filter.Text).ShouldContain("мысли");
    }

    [Fact]
    public async Task Delete_MovesToTrash_UndoRestores()
    {
        // Arrange
        var note = await _service.CreateAsync("Удаляемая", [], TestContext.Current.CancellationToken);
        var page = await OpenAsync();
        Action? undo = null;
        _notices.When(notices => notices.Show("Заметка в корзине", "Удаляемая", FAInfoBarSeverity.Informational, "Отменить", Arg.Any<Action>()))
            .Do(call => undo = call.ArgAt<Action>(4));

        // Act
        await page.DeleteCommand.ExecuteAsync(null);
        var afterDelete = page.Notes.Count;
        undo.ShouldNotBeNull().Invoke();
        var restored = await WaitAsync(() => Task.FromResult(page.Notes.Count == 1));

        // Assert
        afterDelete.ShouldBe(0);
        restored.ShouldBeTrue();
        page.SelectedNote.ShouldNotBeNull().Note.Id.ShouldBe(note.Id);
    }

    [Fact]
    public async Task Trash_ReadOnly_RestoreAndDeleteForever()
    {
        // Arrange
        var kept = await _service.CreateAsync("Вернуть", [], TestContext.Current.CancellationToken);
        var gone = await _service.CreateAsync("Стереть", [], TestContext.Current.CancellationToken);
        await _service.DeleteAsync(kept.Id, cancellationToken: TestContext.Current.CancellationToken);
        await _service.DeleteAsync(gone.Id, cancellationToken: TestContext.Current.CancellationToken);
        _dialogs.ConfirmAsync("Удалить заметку навсегда?", Arg.Any<string>(), "Удалить навсегда").Returns(true);
        var page = await OpenAsync();

        // Act
        await page.ToggleTrashCommand.ExecuteAsync(null);
        var trash = page.Notes.Select(static item => item.Title).ToList();
        var canEditInTrash = page.CanEdit;
        page.SelectedNote = page.Notes.Single(item => item.Note.Id == gone.Id);
        page.ToggleTask(new MarkdownTaskToggle(0, true));
        await page.DeleteForeverCommand.ExecuteAsync(null);
        page.SelectedNote = page.Notes.Single(item => item.Note.Id == kept.Id);
        await page.RestoreCommand.ExecuteAsync(null);

        // Assert
        trash.ShouldBe(["Стереть", "Вернуть"], ignoreOrder: true);
        canEditInTrash.ShouldBeFalse();
        page.ShowTrash.ShouldBeFalse();
        page.Notes.ShouldHaveSingleItem().Note.Id.ShouldBe(kept.Id);
        page.CanEdit.ShouldBeTrue();
        (await _service.GetNotesAsync(deleted: true, cancellationToken: TestContext.Current.CancellationToken)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ToggleTask_InPreview_ChangesBodyAndSaves()
    {
        // Arrange
        await _service.CreateAsync("- [ ] позвонить", [], TestContext.Current.CancellationToken);
        var page = await OpenAsync();
        page.TogglePreviewCommand.Execute(null);

        // Act
        page.ToggleTask(new MarkdownTaskToggle(2, true));
        await page.FlushAsync();

        // Assert
        page.IsEditing.ShouldBeFalse();
        page.PreviewToggleText.ShouldBe("Правка");
        page.Body.ShouldBe("- [x] позвонить");
        (await _service.GetNotesAsync(cancellationToken: TestContext.Current.CancellationToken)).Single().Body.ShouldBe("- [x] позвонить");
    }

    [Fact]
    public void ListWidth_FromLayoutWithinLimits_SavedWhenDragged()
    {
        // Arrange
        var layout = new MemoryLayoutStore();
        layout.SetWidth("notes.list", 100);

        // Act
        var page = new NotesViewModel(_service, layout: layout);
        var restored = page.ListColumnWidth;
        page.ListColumnWidth = new Avalonia.Controls.GridLength(380);

        // Assert
        restored.Value.ShouldBe(NotesViewModel.MinListWidth);
        layout.GetWidth("notes.list").ShouldBe(380);
        new NotesViewModel(_service).ListColumnWidth.Value.ShouldBe(NotesViewModel.DefaultListWidth);
    }

    [Theory]
    [InlineData("# Заголовок\n\nПервая **строка**\n- [ ] дело", "Первая строка · дело")]
    [InlineData("Только заголовок", "")]
    [InlineData("Заголовок\n> цитата\n`код`", "цитата · код")]
    public void SnippetOf_TextAfterTitleWithoutMarkup(string body, string expected)
    {
        // Act
        var snippet = NotesViewModel.SnippetOf(body);

        // Assert
        snippet.ShouldBe(expected);
    }

    private static async Task<bool> WaitAsync(Func<Task<bool>> done)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!await done())
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        return true;
    }

    private DateTimeOffset Tick()
    {
        _now = _now.AddSeconds(1);
        return _now;
    }

    private async Task<NotesViewModel> OpenAsync()
    {
        var page = new NotesViewModel(_service, _dialogs, _notices, () => _now);
        page.OnNavigatedTo();
        await page.PendingLoad;
        return page;
    }
}

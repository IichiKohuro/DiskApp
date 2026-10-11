using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Страница «Теги» (PLAN.md, этап 8.7): загрузка при входе, файлы тега через поиск <c>tag:</c>, создание с цветом,
/// переименование и удаление с подтверждением.
/// </summary>
public sealed class TagsViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.FromHours(3));

    private readonly SampleLibraryService _library = new(static () => Now);
    private readonly ISearchService _search = Substitute.For<ISearchService>();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly IInfoBarService _notices = Substitute.For<IInfoBarService>();

    public TagsViewModelTests()
    {
        _search.SearchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new SearchResult([new FileEntry { Id = 1, FullPath = @"D:\a.txt", Kind = FileKind.Text, Root = "D" }], 1, TimeSpan.Zero));
    }

    [Fact]
    public async Task NavigatedTo_LoadsTagsAndFilesOfFirstTag()
    {
        // Arrange
        var page = Create();

        // Act
        page.OnNavigatedTo();
        await page.PendingLoad;

        // Assert
        page.Tags.ShouldNotBeEmpty();
        page.SelectedTag.ShouldBe(page.Tags[0]);
        page.Files.ShouldHaveSingleItem();
        page.FilesText.ShouldBe("1 файл");
        await _search.Received().SearchAsync("tag:" + DiskApp.UI.Text.QueryText.Quote(page.Tags[0].Name), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateTag_DialogConfirmed_CreatesAndSelects()
    {
        // Arrange
        var page = Create();
        await page.RefreshAsync();
        _dialogs.EditTagAsync("Новый тег", string.Empty, Arg.Any<string?>(), "Создать").Returns(("поездка", "#0078D4"));

        // Act
        await page.CreateTagCommand.ExecuteAsync(null);

        // Assert
        page.SelectedTag.ShouldNotBeNull().Name.ShouldBe("поездка");
        page.SelectedTag.Color.ShouldBe("#0078D4");
    }

    [Fact]
    public async Task CreateTag_DuplicateName_ShowsError()
    {
        // Arrange
        var page = Create();
        await page.RefreshAsync();
        var existing = page.Tags[0].Name;
        _dialogs.EditTagAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>()).Returns((existing, (string?)null));

        // Act
        await page.CreateTagCommand.ExecuteAsync(null);

        // Assert
        _notices.Received(1).Show("Не удалось создать тег", Arg.Is<string>(text => text.Contains(existing)), FluentAvalonia.UI.Controls.FAInfoBarSeverity.Error);
    }

    [Fact]
    public async Task EditTag_Renamed_KeepsSelection()
    {
        // Arrange
        var page = Create();
        await page.RefreshAsync();
        var tag = page.SelectedTag!;
        _dialogs.EditTagAsync("Изменить тег", tag.Name, tag.Color, "Сохранить").Returns(("я-переименован", "#E74856"));

        // Act
        await page.EditTagCommand.ExecuteAsync(null);

        // Assert
        page.SelectedTag.ShouldNotBeNull().Id.ShouldBe(tag.Id);
        page.SelectedTag.Name.ShouldBe("я-переименован");
    }

    [Fact]
    public async Task DeleteTag_Confirmed_RemovesTag()
    {
        // Arrange
        var page = Create();
        await page.RefreshAsync();
        var tag = page.SelectedTag!;
        _dialogs.ConfirmAsync("Удалить тег", Arg.Any<string>(), "Удалить").Returns(true);

        // Act
        await page.DeleteTagCommand.ExecuteAsync(null);

        // Assert
        page.Tags.ShouldNotContain(tag);
        _notices.Received(1).Show("Тег удалён", $"«{tag.Name}»", FluentAvalonia.UI.Controls.FAInfoBarSeverity.Success);
    }

    [Fact]
    public async Task DeleteTag_Cancelled_KeepsTag()
    {
        // Arrange
        var page = Create();
        await page.RefreshAsync();
        var tag = page.SelectedTag!;

        // Act
        await page.DeleteTagCommand.ExecuteAsync(null);

        // Assert
        page.Tags.ShouldContain(tag);
    }

    private TagsViewModel Create() => new(_library, _search, new SampleFileActions(), _dialogs, _notices, tagEditor: null, static () => Now);
}

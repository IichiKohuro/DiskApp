using DiskApp.Contracts.Entries;
using DiskApp.Contracts.Library;
using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.Text;
using DiskApp.UI.ViewModels;

using FluentAvalonia.UI.Controls;

using ContractKind = DiskApp.Contracts.Entries.FileKind;
using FileKind = DiskApp.UI.Models.FileKind;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Авто-теги папок в интерфейсе (PLAN.md, этап 19): поле «Авто-теги», унаследованные чипы, диалог тегов с переходом к папке,
/// «Авто-теги папки…» в «Обзоре» и папки тега на странице «Теги».
/// </summary>
public sealed class AutoTagsUiTests
{
    private const string Work417 = @"D:\Work\417";

    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.FromHours(3));

    private static readonly TagInfo[] Known = [new(1, "ТЗ", "#E74856"), new(2, "Мост", null), new(3, "Мостовой кран", "#0078D4")];

    private static readonly string[] SavedNames = ["ТЗ", "Первый", "Второй"];

    private readonly ILibraryService _library = Substitute.For<ILibraryService>();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();

    [Fact]
    public void Editor_Add_UsesLibraryNameAndColor_SkipsDuplicatesAndHash()
    {
        // Arrange
        var editor = new AutoTagsEditorViewModel([Known[1]], Known);

        // Act
        editor.AddCommand.Execute("тз");
        editor.Text = "  #мост ";
        editor.AddCommand.Execute(null);
        editor.Text = "Новый";
        editor.AddCommand.Execute(null);

        // Assert
        editor.Tags.ShouldBe([new TagLabel("Мост", null), new TagLabel("ТЗ", "#E74856"), new TagLabel("Новый", null)]);
        editor.Text.ShouldBeEmpty();
        editor.IsChanged.ShouldBeTrue();
        editor.HadTags.ShouldBeTrue();
        editor.NewNames.ShouldBe(["Новый"]);
    }

    [Fact]
    public void Editor_RemoveAndAddBack_IsNotChanged()
    {
        // Arrange
        var editor = new AutoTagsEditorViewModel([Known[0]], Known);

        // Act
        editor.RemoveCommand.Execute(editor.Tags[0]);
        var emptied = editor.IsChanged;
        editor.AddCommand.Execute("ТЗ");

        // Assert
        emptied.ShouldBeTrue();
        editor.IsChanged.ShouldBeFalse();
    }

    [Fact]
    public void Editor_Suggestions_ContainText_ExceptAdded()
    {
        // Arrange
        var editor = new AutoTagsEditorViewModel([Known[1]], Known);

        // Act
        editor.Text = "мост";

        // Assert
        editor.Suggestions.ShouldBe(["Мостовой кран"]);
    }

    [Fact]
    public void Editor_Limit_StopsAddingAtMax()
    {
        // Arrange
        var editor = new AutoTagsEditorViewModel([.. Enumerable.Range(1, AutoTagsEditorViewModel.MaxTags).Select(static index => new TagInfo(index, "t" + index, null))], []);

        // Act
        editor.Text = "ещё один";

        // Assert
        editor.AddCommand.CanExecute(null).ShouldBeFalse();
        editor.AddCommand.Execute(null);
        editor.Tags.Count.ShouldBe(AutoTagsEditorViewModel.MaxTags);
    }

    [Fact]
    public async Task Editor_LoadAsync_OwnTagsAndInheritedFromFoldersAbove()
    {
        // Arrange
        _library.GetTagsAsync(Arg.Any<CancellationToken>()).Returns(Known);
        _library.GetAutoTagsAsync(Arg.Any<CancellationToken>()).Returns([
            new FolderTagsInfo(@"D:\Work", [Known[1]]),
            new FolderTagsInfo(Work417 + @"\", [Known[0]]),
            new FolderTagsInfo(@"D:\Work\4170", [Known[2]]),
        ]);

        // Act
        var editor = await AutoTagsEditorViewModel.LoadAsync(_library, Work417);

        // Assert
        editor.Names.ShouldBe(["ТЗ"]);
        editor.InheritedText.ShouldBe("Уже получает от папок выше: Мост (Work)");
        editor.HasInherited.ShouldBeTrue();
    }

    [Fact]
    public async Task Editor_SaveAsync_CreatesNewTagsWithPaletteColorsFirst()
    {
        // Arrange
        var editor = new AutoTagsEditorViewModel([], Known);
        editor.AddCommand.Execute("ТЗ");
        editor.AddCommand.Execute("Первый");
        editor.AddCommand.Execute("Второй");
        _library.SetAutoTagsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(new FolderTagsInfo(Work417, []));

        // Act
        await editor.SaveAsync(_library, Work417);

        // Assert: цвета новых тегов — следующие свободные в палитре, разные
        var created = _library.ReceivedCalls().Where(static call => call.GetMethodInfo().Name == nameof(ILibraryService.CreateTagAsync)).Select(static call => ((string)call.GetArguments()[0]!, (string?)call.GetArguments()[1])).ToArray();
        created.Select(static call => call.Item1).ShouldBe(["Первый", "Второй"]);
        created.Select(static call => call.Item2).ShouldAllBe(color => color != null);
        created[0].Item2.ShouldNotBe(created[1].Item2);
        await _library.Received(1).SetAutoTagsAsync(Work417, Arg.Is<IReadOnlyList<string>>(tags => tags.SequenceEqual(SavedNames)), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(@"D:\Work\417\план.docx", Work417, true)]
    [InlineData(@"D:\Work\417", @"D:\Work\417\", true)]
    [InlineData(@"d:\work\417\Чертежи", Work417, true)]
    [InlineData(@"D:\Work\417 ТЗ\план.docx", Work417, false)]
    [InlineData(@"D:\Work\4170", Work417, false)]
    [InlineData(@"D:\Work", Work417, false)]
    [InlineData("/home/user/417/a.txt", "/home/user/417", true)]
    public void PathText_IsSameOrInside(string path, string folder, bool expected)
        => PathText.IsSameOrInside(path, folder).ShouldBe(expected);

    [Fact]
    public void InheritedAt_NearestFolderFirst_EachTagOnce()
    {
        // Arrange
        FolderTagsInfo[] folders =
        [
            new(@"D:\Work", [Known[0], Known[1]]),
            new(Work417, [Known[0]]),
            new(@"D:\Work\417 ТЗ", [Known[2]]),
        ];

        // Act
        var inherited = FolderTagsInfo.InheritedAt(folders, Work417 + @"\Чертежи\лист.pdf");

        // Assert
        inherited.ShouldBe([new TagLabel("ТЗ", "#E74856", Work417), new TagLabel("Мост", null, @"D:\Work")]);
    }

    [Fact]
    public void FileEntry_TagLabels_OwnThenInherited_WithHint()
    {
        // Arrange
        var entry = new FileEntry
        {
            FullPath = Work417 + @"\план.docx",
            Kind = FileKind.Document,
            Root = "Work",
            Tags = ["срочно"],
            InheritedTags = [new TagLabel("ТЗ", "#E74856", Work417)],
        };

        // Act
        var labels = entry.TagLabels;

        // Assert
        labels.Select(static label => (label.Name, label.IsInherited)).ShouldBe([("срочно", false), ("ТЗ", true)]);
        labels[0].Hint.ShouldBeNull();
        labels[1].Hint.ShouldBe(@"Авто-тег папки D:\Work\417");
        entry.HasAnyTags.ShouldBeTrue();
        new SearchHit(entry with { Tags = [] }, string.Empty, 0, Now).HasTags.ShouldBeTrue("одних унаследованных хватает для строки тегов");
    }

    [Fact]
    public void ToEntry_MapsInheritedTags()
    {
        // Arrange
        var dto = new EntryDto
        {
            Id = 7,
            Name = "план.docx",
            FullPath = Work417 + @"\план.docx",
            RelPath = @"417\план.docx",
            Kind = ContractKind.Document,
            InheritedTags = [new InheritedTagDto(1, "ТЗ", "#E74856", Work417)],
        };

        // Act
        var entry = ApiSearchService.ToEntry(dto, "Work");

        // Assert
        entry.InheritedTags.ShouldBe([new TagLabel("ТЗ", "#E74856", Work417)]);
        entry.Tags.ShouldBeEmpty();
    }

    [Fact]
    public async Task TagPicker_GoToFolder_ClosesWithoutSavingAndOpensFolder()
    {
        // Arrange
        var commands = Substitute.For<IAppCommands>();
        var entry = new FileEntry
        {
            Id = 7,
            FullPath = Work417 + @"\план.docx",
            Kind = FileKind.Document,
            Root = "Work",
            InheritedTags = [new TagLabel("ТЗ", "#E74856", Work417), new TagLabel("Мост", null, @"D:\Work")],
        };
        _library.GetTagsAsync(Arg.Any<CancellationToken>()).Returns(Known);
        TagPickerViewModel? shown = null;
        var closeRequested = false;
        _dialogs.ShowTagPickerAsync(Arg.Any<TagPickerViewModel>()).Returns(call =>
        {
            shown = call.Arg<TagPickerViewModel>();
            shown.CloseRequested += (_, _) => closeRequested = true;
            shown.Choices[0].IsChecked = true;
            shown.GoToFolderCommand.Execute(Work417);
            return false;
        });
        var editor = new TagEditor(_library, _dialogs, commands);

        // Act
        var result = await editor.EditAsync([entry]);

        // Assert
        shown.ShouldNotBeNull().Inherited.Select(static group => group.FolderName).ShouldBe(["417", "Work"]);
        shown.HasInherited.ShouldBeTrue();
        closeRequested.ShouldBeTrue();
        result.ShouldBeNull();
        commands.Received(1).BrowseFolder(Work417);
        await _library.DidNotReceive().SetTagsAsync(Arg.Any<FileEntry>(), Arg.Any<IReadOnlyList<long>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TagEditor_EditAutoTags_SavesOnlyChanges(bool change)
    {
        // Arrange
        _library.GetTagsAsync(Arg.Any<CancellationToken>()).Returns(Known);
        _library.GetAutoTagsAsync(Arg.Any<CancellationToken>()).Returns([new FolderTagsInfo(Work417, [Known[0]])]);
        _library.SetAutoTagsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(new FolderTagsInfo(Work417, []));
        string? title = null;
        _dialogs.ShowAutoTagsAsync(Arg.Any<string>(), Arg.Any<AutoTagsEditorViewModel>()).Returns(call =>
        {
            title = call.ArgAt<string>(0);
            if (change)
            {
                call.ArgAt<AutoTagsEditorViewModel>(1).AddCommand.Execute("Мост");
            }

            return true;
        });

        // Act
        var changed = await new TagEditor(_library, _dialogs).EditAutoTagsAsync(Work417);

        // Assert
        title.ShouldBe("417");
        changed.ShouldBe(change);
        await _library.Received(change ? 1 : 0).SetAutoTagsAsync(Work417, Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Browse_AutoTagsOfSelectedFolder_OrOpenFolderWhenNothingSelected()
    {
        // Arrange
        var tags = Substitute.For<ITagEditor>();
        tags.EditAutoTagsAsync(Arg.Any<string>()).Returns(true);
        var notices = Substitute.For<IInfoBarService>();
        var browse = new BrowseViewModel(new SampleBrowseService(static () => Now), new SampleRootsService(), new SampleSearchService(static () => Now), new SampleFileActions(), _dialogs, notices, static () => Now)
        {
            TagEditor = tags,
        };
        await browse.Initialization;
        await browse.NavigateAsync(@"D:\Projects\DiskApp");
        var folder = browse.Items.First(static hit => hit.Entry.Kind == FileKind.Folder);
        var file = browse.Items.First(static hit => hit.Entry.Kind != FileKind.Folder);

        // Act
        browse.UpdateSelection([file]);
        var onFile = browse.CanEditAutoTags;
        browse.UpdateSelection([folder]);
        await browse.EditAutoTagsCommand.ExecuteAsync(null);
        browse.UpdateSelection([]);
        var onNothing = browse.CanEditAutoTags;
        await browse.EditAutoTagsCommand.ExecuteAsync(null);

        // Assert
        onFile.ShouldBeFalse();
        onNothing.ShouldBeTrue();
        Received.InOrder(() =>
        {
            tags.EditAutoTagsAsync(folder.Entry.FullPath);
            tags.EditAutoTagsAsync(@"D:\Projects\DiskApp");
        });
        notices.Received(2).Show("Авто-теги сохранены", Arg.Any<string>(), FAInfoBarSeverity.Success);
    }

    [Fact]
    public async Task Browse_MenuNamesTargetFolder_TreeMenuTagsRootItself()
    {
        // Arrange: рабочая папка, добавленная корнем, видна только в дереве — авто-теги ей задаются оттуда
        var tags = Substitute.For<ITagEditor>();
        tags.EditAutoTagsAsync(Arg.Any<string>()).Returns(false);
        var browse = new BrowseViewModel(new SampleBrowseService(static () => Now), new SampleRootsService(), new SampleSearchService(static () => Now), new SampleFileActions(), _dialogs, null, static () => Now)
        {
            TagEditor = tags,
        };
        await browse.Initialization;
        await browse.NavigateAsync(@"D:\Projects\DiskApp");
        var folder = browse.Items.First(static hit => hit.Entry.Kind == FileKind.Folder);
        var root = browse.Tree[0];

        // Act
        browse.UpdateSelection([folder]);
        var onFolder = browse.AutoTagsMenuText;
        browse.UpdateSelection([]);
        var onOpenFolder = browse.AutoTagsMenuText;
        await browse.EditFolderAutoTagsCommand.ExecuteAsync(root);
        await browse.EditFolderAutoTagsCommand.ExecuteAsync(new FolderNode("…", string.Empty, FluentIcons.Common.Symbol.MoreHorizontal, null));

        // Assert
        onFolder.ShouldBe($"Авто-теги папки «{folder.Entry.Name}»…");
        onOpenFolder.ShouldBe("Авто-теги папки «DiskApp»…");
        await tags.Received(1).EditAutoTagsAsync(root.Path);
        await tags.Received(1).EditAutoTagsAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task TagsPage_ShowsFoldersWhereTagIsAuto_AndOpensThem()
    {
        // Arrange
        var commands = Substitute.For<IAppCommands>();
        _library.GetTagsAsync(Arg.Any<CancellationToken>()).Returns(Known);
        _library.GetAutoTagsAsync(Arg.Any<CancellationToken>()).Returns([
            new FolderTagsInfo(@"D:\Work\418", [Known[0]]),
            new FolderTagsInfo(Work417, [Known[0], Known[1]]),
            new FolderTagsInfo(@"D:\Work\Мосты", [Known[1]]),
        ]);
        var page = new TagsViewModel(_library, new SampleSearchService(static () => Now), new SampleFileActions(), _dialogs, null, null, static () => Now, commands);

        // Act
        await page.RefreshAsync();
        var folders = page.AutoTagFolders.Select(static folder => folder.Name).ToArray();
        page.OpenAutoTagFolderCommand.Execute(page.AutoTagFolders[0]);

        // Assert
        page.SelectedTag.ShouldBe(Known[0]);
        folders.ShouldBe(["417", "418"]);
        page.HasAutoTagFolders.ShouldBeTrue();
        commands.Received(1).BrowseFolder(Work417);
    }
}

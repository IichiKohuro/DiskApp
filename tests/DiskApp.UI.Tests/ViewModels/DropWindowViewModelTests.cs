using Avalonia.Input;

using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Окно Drag&amp;Drop (PLAN.md, этап 18): разовая подсказка о новом поведении значка (18.1); бросок в основную
/// папку-накопитель — действие как в Проводнике, итог, отмена, состояние без накопителя (18.2).
/// </summary>
public sealed class DropWindowViewModelTests
{
    private const string Downloads = @"C:\Users\user\Downloads";
    private const DragDropEffects Any = DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link;

    private readonly ITipsService _tips = Substitute.For<ITipsService>();
    private readonly HotkeyStatus _hotkeys = new();
    private readonly SampleOperationsService _operations = new();
    private readonly IAppCommands _commands = Substitute.For<IAppCommands>();
    private SampleSortingSettings _sorting = new(new SortingFolders([new SortingFolderInfo(Downloads, "Загрузки"), new SortingFolderInfo(@"D:\Skype")], []));

    [Theory]
    [InlineData(@"C:\Users\user\Desktop\a.pdf", KeyModifiers.None, DropMode.Move)]
    [InlineData(@"D:\Photos\b.jpg", KeyModifiers.None, DropMode.Copy)]
    [InlineData(@"D:\Photos\b.jpg", KeyModifiers.Shift, DropMode.Move)]
    [InlineData(@"C:\Users\user\Desktop\a.pdf", KeyModifiers.Control, DropMode.Copy)]
    [InlineData(@"\\server\share\c.docx", KeyModifiers.None, DropMode.Copy)]
    public void Decide_LikeExplorer_SameVolumeMovesOtherCopiesModifiersOverride(string source, KeyModifiers modifiers, DropMode expected)
    {
        // Act
        var decision = DropWindowViewModel.Decide([source], Downloads, Any, modifiers);

        // Assert
        decision.Mode.ShouldBe(expected);
        decision.Refusal.ShouldBe(DropRefusal.None);
    }

    [Fact]
    public void Decide_SourceAllowsOnlyCopy_CopiesEvenOnSameVolume()
    {
        // Act: мессенджер или браузер разрешает только копию — его файл не уносится
        var decision = DropWindowViewModel.Decide([@"C:\Users\user\AppData\Roaming\Telegram\file.pdf"], Downloads, DragDropEffects.Copy, KeyModifiers.Shift);

        // Assert
        decision.Mode.ShouldBe(DropMode.Copy);
    }

    [Fact]
    public void Decide_MixedVolumes_Copies()
    {
        // Act
        var decision = DropWindowViewModel.Decide([@"C:\a.txt", @"E:\b.txt"], Downloads, Any, KeyModifiers.None);

        // Assert
        decision.Mode.ShouldBe(DropMode.Copy);
    }

    [Theory]
    [InlineData(@"C:\Users\user\Downloads\a.pdf", DropRefusal.AlreadyThere)]
    [InlineData(@"C:\Users\user\Downloads", DropRefusal.IntoItself)]
    [InlineData(@"C:\Users", DropRefusal.IntoItself)]
    public void Decide_Refuses_WhatMakesNoSense(string source, DropRefusal expected)
    {
        // Act
        var decision = DropWindowViewModel.Decide([source], Downloads, Any, KeyModifiers.None);

        // Assert
        decision.Mode.ShouldBeNull();
        decision.Refusal.ShouldBe(expected);
    }

    [Fact]
    public async Task RefreshAsync_UsesMainInboxOrFirst()
    {
        // Arrange
        var viewModel = Create();

        // Act
        await viewModel.RefreshAsync();
        var first = viewModel.Inbox?.Path;
        _sorting = new SampleSortingSettings(new SortingFolders([new SortingFolderInfo(Downloads), new SortingFolderInfo(@"D:\Skype")], [], MainInbox: @"D:\Skype"));
        var withMain = Create();
        await withMain.RefreshAsync();

        // Assert
        first.ShouldBe(Downloads);
        viewModel.ZoneText.ShouldBe("Они попадут в «Загрузки»");
        withMain.Inbox!.Path.ShouldBe(@"D:\Skype");
    }

    [Fact]
    public async Task PreviewDrop_ShowsActionInTitle_EndPreviewRestores()
    {
        // Arrange
        var viewModel = Create();
        await viewModel.RefreshAsync();

        // Act
        var decision = viewModel.PreviewDrop([@"D:\Photos\b.jpg"], Any, KeyModifiers.None);
        var during = viewModel.ZoneTitle;
        viewModel.EndPreview();

        // Assert
        decision.Mode.ShouldBe(DropMode.Copy);
        during.ShouldBe("Скопировать в «Загрузки»");
        viewModel.IsDragOver.ShouldBeFalse();
        viewModel.ZoneTitle.ShouldBe("Перетащите файлы сюда");
    }

    [Fact]
    public async Task DropAsync_MovesIntoMainInbox_ShowsResultWithUndo()
    {
        // Arrange
        var viewModel = Create();
        await viewModel.RefreshAsync();
        var settled = 0;
        viewModel.Settled += (_, _) => settled++;

        // Act
        await viewModel.DropAsync([@"C:\Users\user\Desktop\a.pdf", @"C:\Users\user\Desktop\b.pdf"], Any, KeyModifiers.None);

        // Assert
        _operations.Dropped.ShouldHaveSingleItem().ShouldBe(new DropOperation([@"C:\Users\user\Desktop\a.pdf", @"C:\Users\user\Desktop\b.pdf"], Downloads, Copy: false), new DropOperationComparer());
        viewModel.Result.ShouldBe("2 файла → Загрузки");
        viewModel.CanUndo.ShouldBeTrue();
        viewModel.IsError.ShouldBeFalse();
        settled.ShouldBe(1, "после удачного броска окно может спрятаться само");
    }

    [Fact]
    public async Task Undo_CallsOperationUndo()
    {
        // Arrange
        var viewModel = Create();
        await viewModel.RefreshAsync();
        await viewModel.DropAsync([@"C:\Users\user\Desktop\a.pdf"], Any, KeyModifiers.None);

        // Act
        await viewModel.UndoCommand.ExecuteAsync(null);

        // Assert
        _operations.Undone.ShouldHaveSingleItem();
        viewModel.Result.ShouldBe("Отменено");
        viewModel.CanUndo.ShouldBeFalse();
    }

    [Fact]
    public async Task DropAsync_AlreadyInInbox_DoesNothing()
    {
        // Arrange
        var viewModel = Create();
        await viewModel.RefreshAsync();

        // Act
        await viewModel.DropAsync([Downloads + @"\a.pdf"], Any, KeyModifiers.None);

        // Assert
        _operations.Dropped.ShouldBeEmpty();
        viewModel.HasResult.ShouldBeFalse();
    }

    [Fact]
    public async Task NoInboxes_OffersSortingPageAndRefusesDrop()
    {
        // Arrange
        _sorting = new SampleSortingSettings(SortingFolders.Empty);
        var viewModel = Create();
        await viewModel.RefreshAsync();
        var closed = 0;
        viewModel.CloseRequested += (_, _) => closed++;

        // Act
        await viewModel.DropAsync([@"C:\a.pdf"], Any, KeyModifiers.None);
        viewModel.OpenSortingCommand.Execute(null);

        // Assert
        viewModel.HasNoInbox.ShouldBeTrue();
        viewModel.ZoneTitle.ShouldBe("Нет папки-накопителя");
        _operations.Dropped.ShouldBeEmpty();
        _commands.Received(1).NavigateTo(PageKeys.Inbox);
        closed.ShouldBe(1);
    }

    [Fact]
    public async Task OnHidden_ClearsResult()
    {
        // Arrange
        var viewModel = Create();
        await viewModel.RefreshAsync();
        await viewModel.DropAsync([@"C:\Users\user\Desktop\a.pdf"], Any, KeyModifiers.None);

        // Act
        viewModel.OnHidden();

        // Assert
        viewModel.HasResult.ShouldBeFalse();
        viewModel.CanUndo.ShouldBeFalse();
    }

    [Fact]
    public void OnShown_FirstTime_ExplainsTrayClicksWithQuickSearchHotkey()
    {
        // Arrange
        _tips.ShouldShow(Tips.TrayClicks).Returns(true);
        _hotkeys.Set(HotkeyTarget.QuickSearch, "Ctrl + Alt + D", string.Empty);
        var viewModel = Create();

        // Act
        viewModel.OnShown();

        // Assert
        viewModel.HasHint.ShouldBeTrue();
        viewModel.Hint.ShouldContain("двойным щелчком");
        viewModel.Hint.ShouldContain("Ctrl\u00A0+\u00A0Alt\u00A0+\u00A0D", Case.Sensitive, "сочетание не переносится по строкам");
        _tips.DidNotReceive().MarkShown(Arg.Any<string>());
    }

    [Fact]
    public void OnShown_QuickSearchHotkeyUnavailable_PointsToMenu()
    {
        // Arrange
        _tips.ShouldShow(Tips.TrayClicks).Returns(true);
        _hotkeys.Set(HotkeyTarget.QuickSearch, string.Empty, "занято");
        var viewModel = Create();

        // Act
        viewModel.OnShown();

        // Assert
        viewModel.Hint.ShouldContain("в меню значка");
    }

    [Fact]
    public void OnHidden_AfterHint_MarksTipShownAndClearsIt()
    {
        // Arrange
        _tips.ShouldShow(Tips.TrayClicks).Returns(true);
        var viewModel = Create();
        viewModel.OnShown();

        // Act
        viewModel.OnHidden();

        // Assert
        _tips.Received(1).MarkShown(Tips.TrayClicks);
        viewModel.HasHint.ShouldBeFalse();
    }

    [Fact]
    public void OnShown_TipAlreadyShown_ShowsNothing()
    {
        // Arrange
        _tips.ShouldShow(Tips.TrayClicks).Returns(false);
        var viewModel = Create();

        // Act
        viewModel.OnShown();
        viewModel.OnHidden();

        // Assert
        viewModel.HasHint.ShouldBeFalse();
        _tips.DidNotReceive().MarkShown(Arg.Any<string>());
    }

    [Fact]
    public void Close_RaisesCloseRequested()
    {
        // Arrange
        var viewModel = Create();
        var raised = 0;
        viewModel.CloseRequested += (_, _) => raised++;

        // Act
        viewModel.CloseCommand.Execute(null);

        // Assert
        raised.ShouldBe(1);
    }

    private DropWindowViewModel Create() => new(_hotkeys, _tips, _sorting, _operations, new ImmediateDispatcher(), _commands);

    private sealed class DropOperationComparer : IEqualityComparer<DropOperation>
    {
        public bool Equals(DropOperation? x, DropOperation? y)
            => x is not null && y is not null && x.Sources.SequenceEqual(y.Sources) && x.Destination == y.Destination && x.Copy == y.Copy;

        public int GetHashCode(DropOperation obj) => obj.Destination.GetHashCode(StringComparison.Ordinal);
    }
}

using DiskApp.Contracts.Operations;
using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

using FluentAvalonia.UI.Controls;


namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Операции в интерфейсе (PLAN.md, этап 8.4): очередь с ходом, отмена, возврат результата и <c>Ctrl+Z</c>,
/// уведомление «Отменить», диалог конфликта; перевод операций API в модель интерфейса.
/// </summary>
public sealed class OperationsViewModelTests
{
    private readonly SampleOperationsService _service = new();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly IInfoBarService _notices = Substitute.For<IInfoBarService>();

    [Fact]
    public async Task Enqueue_Copy_ShowsActiveItemSummaryAndBadge()
    {
        // Arrange
        var page = Create();

        // Act
        var info = await page.EnqueueAsync(new NewOperation(FileOperationKind.Copy, [@"D:\a.txt", @"D:\b.txt"], @"E:\Архив"));
        _service.Report(info! with { State = FileOperationState.Running, ProcessedBytes = 50, TotalBytes = 200, BytesPerSecond = 1024 * 1024, RemainingSeconds = 3 });

        // Assert
        var item = page.Items.ShouldHaveSingleItem();
        item.Title.ShouldBe("Копирование 2 элементов в «Архив»");
        item.ProgressPercent.ShouldBe(25);
        item.StateText.ShouldStartWith("25");
        item.DetailText.ShouldContain("1 МБ/с");
        item.DetailText.ShouldContain("осталось 3 с");
        page.ActiveCount.ShouldBe(1);
        page.BadgeValue.ShouldBe(1);
        page.Summary.ShouldStartWith("1 операция · 25");
    }

    [Fact]
    public async Task Completed_Undoable_NotifiesWithUndoActionThatWorks()
    {
        // Arrange
        var page = Create();
        var info = await page.EnqueueAsync(new NewOperation(FileOperationKind.Move, [@"D:\a.txt"], @"D:\Папка"));
        Action? undo = null;
        _notices.When(notices => notices.Show("Готово", Arg.Any<string>(), FAInfoBarSeverity.Success, "Отменить", Arg.Any<Action>()))
            .Do(call => undo = call.ArgAt<Action>(4));

        // Act
        _service.Report(info! with { State = FileOperationState.Completed, CanUndo = true });
        undo.ShouldNotBeNull();
        undo();
        await Task.Delay(20, TestContext.Current.CancellationToken);

        // Assert
        _service.Undone.ShouldBe([info.Id]);
        page.ActiveCount.ShouldBe(0);
        page.Items.Single(item => item.Info.Id == info.Id).CanUndo.ShouldBeFalse();
    }

    [Fact]
    public async Task UndoLast_AfterRename_UndoesItOnce()
    {
        // Arrange
        var page = Create();
        var rename = new OperationInfo { Id = Guid.NewGuid(), Kind = FileOperationKind.Rename, State = FileOperationState.Completed, CanUndo = true, Sources = [@"D:\a.txt"] };
        _service.Report(rename);

        // Act
        var first = await page.UndoLastAsync();
        var second = await page.UndoLastAsync();

        // Assert
        first.ShouldBeTrue();
        second.ShouldBeFalse();
        _service.Undone.ShouldBe([rename.Id]);
        _notices.Received(1).Show("Нечего отменять", Arg.Any<string>(), FAInfoBarSeverity.Informational);
    }

    [Fact]
    public async Task Failed_StartedHere_ShowsError()
    {
        // Arrange
        var page = Create();
        var info = await page.EnqueueAsync(new NewOperation(FileOperationKind.Copy, [@"D:\a.txt"], @"E:\"));

        // Act
        _service.Report(info! with { State = FileOperationState.Failed, Message = "Нет места на диске" });

        // Assert
        page.Items[0].IsFailed.ShouldBeTrue();
        page.Items[0].DetailText.ShouldBe("Нет места на диске");
        _notices.Received(1).Show("Операция не выполнена", Arg.Is<string>(text => text.EndsWith("Нет места на диске")), FAInfoBarSeverity.Error);
    }

    [Fact]
    public void Completed_StartedElsewhere_NoNotice()
    {
        // Arrange
        var page = Create();
        var info = new OperationInfo { Id = Guid.NewGuid(), Kind = FileOperationKind.Delete, State = FileOperationState.Running, Sources = [@"D:\a.txt"] };
        _service.Report(info);

        // Act
        _service.Report(info with { State = FileOperationState.Completed });

        // Assert
        page.Items.ShouldHaveSingleItem().StateText.ShouldBe("Готово");
        _notices.DidNotReceiveWithAnyArgs().Show(default!, default!, default);
    }

    [Fact]
    public async Task Conflict_AskedAndDecided()
    {
        // Arrange
        var page = Create();
        var info = await page.EnqueueAsync(new NewOperation(FileOperationKind.Copy, [@"D:\a.txt"], @"E:\"));
        var conflict = new ConflictInfo(info!.Id, @"D:\a.txt", @"E:\a.txt", 10, DateTimeOffset.UnixEpoch, 20, DateTimeOffset.UnixEpoch);
        _dialogs.ResolveConflictAsync(conflict).Returns(new ConflictAnswer(ConflictChoice.KeepBoth, ApplyToAll: true));

        // Act
        _service.RaiseConflict(conflict);
        await Task.Delay(20, TestContext.Current.CancellationToken);

        // Assert
        _service.Decisions.ShouldHaveSingleItem().ShouldBe((info.Id, new ConflictAnswer(ConflictChoice.KeepBoth, true)));
    }

    [Fact]
    public void Conflict_OfRename_IsLeftToQuickSearch()
    {
        // Arrange
        var page = Create();
        var rename = new OperationInfo { Id = Guid.NewGuid(), Kind = FileOperationKind.Rename, State = FileOperationState.Running, Sources = [@"D:\a.txt"] };
        _service.Report(rename);

        // Act
        _service.RaiseConflict(new ConflictInfo(rename.Id, @"D:\a.txt", @"D:\b.txt", 1, DateTimeOffset.UnixEpoch, 1, DateTimeOffset.UnixEpoch));

        // Assert
        _dialogs.DidNotReceiveWithAnyArgs().ResolveConflictAsync(default!);
        _service.Decisions.ShouldBeEmpty();
        page.Items[0].StateText.ShouldStartWith("Ждёт решения");
    }

    [Fact]
    public async Task CancelAndClearFinished_UpdateList()
    {
        // Arrange
        var page = Create();
        var first = await page.EnqueueAsync(new NewOperation(FileOperationKind.Copy, [@"D:\a.txt"], @"E:\"));
        await page.EnqueueAsync(new NewOperation(FileOperationKind.CreateFolder, [], @"E:\Архив", "Новая папка"));

        // Act
        await page.CancelCommand.ExecuteAsync(page.Items.Single(item => item.Info.Id == first!.Id));
        var hasFinished = page.HasFinished;
        page.ClearFinishedCommand.Execute(null);

        // Assert
        _service.Cancelled.ShouldBe([first!.Id]);
        hasFinished.ShouldBeTrue();
        page.Items.ShouldHaveSingleItem().Title.ShouldBe("Новая папка в «Архив»");
        page.ActiveCount.ShouldBe(1);
    }

    [Fact]
    public void Enums_MirrorContracts()
    {
        // Act & Assert
        Enum.GetNames<FileOperationKind>().ShouldBe(Enum.GetNames<OperationKind>());
        Enum.GetNames<FileOperationState>().ShouldBe(Enum.GetNames<OperationState>());
        Enum.GetNames<ConflictChoice>().ShouldBe(Enum.GetNames<ConflictResolution>());
    }

    [Fact]
    public void ApiOperations_ToInfo_MapsProgressAndConflict()
    {
        // Arrange
        var id = Guid.NewGuid();
        var dto = new OperationDto(id, OperationKind.Move, OperationState.WaitingDecision, [@"D:\a.txt"], @"E:\", DateTimeOffset.UnixEpoch, null,
            new OperationProgressDto(id, 1, 4, 100, 400, @"D:\a.txt", 50, 6), null, CanUndo: false,
            new ConflictDto(id, @"D:\a.txt", @"E:\a.txt", 100, DateTimeOffset.UnixEpoch, 90, DateTimeOffset.UnixEpoch));

        // Act
        var info = ApiOperationsService.ToInfo(dto);

        // Assert
        info.Kind.ShouldBe(FileOperationKind.Move);
        info.State.ShouldBe(FileOperationState.WaitingDecision);
        info.Fraction.ShouldBe(0.25);
        info.IsActive.ShouldBeTrue();
        info.PendingConflict.ShouldNotBeNull().TargetSize.ShouldBe(90);
    }

    [Fact]
    public void MainNotice_WithAction_RunsItAndCloses()
    {
        // Arrange
        var search = new SampleSearchService();
        var main = new MainViewModel(
            new HomeViewModel(search, Substitute.For<IAppCommands>(), new HotkeyStatus()),
            new QuickSearchViewModel(search),
            new SettingsViewModel(Substitute.For<IThemeService>(), search, new HotkeyStatus()),
            search);
        var ran = false;

        // Act
        main.ShowNotice("Готово", "Перемещение", FAInfoBarSeverity.Success, "Отменить", () => ran = true);
        var shown = (main.IsNoticeOpen, main.HasNoticeAction, main.NoticeActionText);
        main.RunNoticeActionCommand.Execute(null);

        // Assert
        shown.ShouldBe((true, true, "Отменить"));
        ran.ShouldBeTrue();
        main.IsNoticeOpen.ShouldBeFalse();
        main.HasNoticeAction.ShouldBeFalse();
    }

    private OperationsViewModel Create() => new(_service, new ImmediateDispatcher(), _dialogs, _notices);
}

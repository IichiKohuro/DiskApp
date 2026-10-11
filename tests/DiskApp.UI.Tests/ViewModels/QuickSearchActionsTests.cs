using DiskApp.UI.Input;
using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Действия быстрого поиска (PLAN.md, этапы 7.2, 7.3, 7.7): команды над выбранным файлом, панель переименования
/// и подтверждения корзины, уведомления, история по <c>↑</c>, переходы к началу и концу списка.
/// </summary>
public sealed class QuickSearchActionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 15, 0, 0, TimeSpan.FromHours(3));

    private readonly SampleFileActions _actions = new();

    [Fact]
    public async Task Reveal_Selected_CallsServerAndRequestsDismiss()
    {
        // Arrange
        using var viewModel = await CreateWithQueryAsync("бюджет");
        var dismissed = 0;
        viewModel.DismissRequested += (_, _) => dismissed++;

        // Act
        await viewModel.ExecuteAsync(QuickSearchCommand.Reveal);

        // Assert
        _actions.Calls.ShouldHaveSingleItem().ShouldStartWith("Reveal: ");
        _actions.Calls[0].ShouldEndWith("Бюджет 2026.xlsx");
        dismissed.ShouldBe(1);
        viewModel.IsNoticeOpen.ShouldBeFalse();
    }

    [Theory]
    [InlineData(QuickSearchCommand.OpenWith, "OpenWith: ")]
    [InlineData(QuickSearchCommand.Properties, "Properties: ")]
    [InlineData(QuickSearchCommand.RunAsAdmin, "RunAsAdmin: ")]
    public async Task ShellCommands_Selected_CallMatchingAction(QuickSearchCommand command, string call)
    {
        // Arrange
        using var viewModel = await CreateWithQueryAsync("бюджет");

        // Act
        await viewModel.ExecuteAsync(command);

        // Assert
        _actions.Calls.ShouldHaveSingleItem().ShouldStartWith(call);
    }

    [Fact]
    public async Task Reveal_ServerFails_ShowsErrorAndKeepsWindow()
    {
        // Arrange
        using var viewModel = await CreateWithQueryAsync("бюджет");
        _actions.FailWith = new InvalidOperationException("Файл больше не существует.");
        var dismissed = false;
        viewModel.DismissRequested += (_, _) => dismissed = true;

        // Act
        await viewModel.ExecuteAsync(QuickSearchCommand.Reveal);

        // Assert
        dismissed.ShouldBeFalse();
        viewModel.IsNoticeOpen.ShouldBeTrue();
        viewModel.NoticeSeverity.ShouldBe(FAInfoBarSeverity.Error);
        viewModel.NoticeTitle.ShouldBe("Не удалось показать в папке");
        viewModel.NoticeMessage.ShouldBe("Файл больше не существует.");
    }

    [Fact]
    public async Task CopyPath_Selected_CopiesAndShowsSuccess()
    {
        // Arrange
        using var viewModel = await CreateWithQueryAsync("бюджет");

        // Act
        await viewModel.ExecuteAsync(QuickSearchCommand.CopyPath);
        await viewModel.ExecuteAsync(QuickSearchCommand.CopyFiles);

        // Assert
        _actions.Calls.Select(static call => call.Split(':')[0]).ShouldBe(["CopyPaths", "CopyFiles"]);
        viewModel.IsNoticeOpen.ShouldBeTrue();
        viewModel.NoticeSeverity.ShouldBe(FAInfoBarSeverity.Success);
        viewModel.NoticeTitle.ShouldBe("Скопировано");
    }

    [Fact]
    public async Task Rename_NewName_ReplacesRowAndKeepsSelection()
    {
        // Arrange
        using var viewModel = await CreateWithQueryAsync("отчёт");
        viewModel.MoveSelection(1);
        var original = viewModel.SelectedResult!.Entry;

        // Act
        await viewModel.ExecuteAsync(QuickSearchCommand.Rename);
        var prompt = viewModel.PendingPrompt;
        var selection = viewModel.RenameSelectionLength;
        viewModel.RenameText = "Отчёт Q2.docx";
        await viewModel.ConfirmPendingActionAsync();

        // Assert
        prompt.ShouldBe($"Новое имя для «{original.Name}»");
        selection.ShouldBe(original.Name.Length - ".docx".Length, "выделено имя без расширения");
        viewModel.HasPendingAction.ShouldBeFalse();
        viewModel.SelectedIndex.ShouldBe(1);
        viewModel.SelectedResult!.Entry.Name.ShouldBe("Отчёт Q2.docx");
        viewModel.SelectedRow.ShouldBeSameAs(viewModel.SelectedResult);
        viewModel.Rows.ShouldContain(viewModel.SelectedResult);
        _actions.Calls.ShouldHaveSingleItem().ShouldEndWith(" → Отчёт Q2.docx");
        viewModel.NoticeTitle.ShouldBe("Переименовано");
    }

    [Fact]
    public async Task Rename_InvalidName_WarnsAndKeepsPanelOpen()
    {
        // Arrange
        using var viewModel = await CreateWithQueryAsync("бюджет");
        await viewModel.ExecuteAsync(QuickSearchCommand.Rename);

        // Act
        viewModel.RenameText = "плохое:имя.xlsx";
        await viewModel.ConfirmPendingActionAsync();

        // Assert
        viewModel.IsRenaming.ShouldBeTrue();
        viewModel.NoticeSeverity.ShouldBe(FAInfoBarSeverity.Warning);
        _actions.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Rename_Escape_ClosesPanelWithoutChanges()
    {
        // Arrange
        using var viewModel = await CreateWithQueryAsync("бюджет");
        await viewModel.ExecuteAsync(QuickSearchCommand.Rename);

        // Act
        await viewModel.ExecuteAsync(QuickSearchCommand.Escape);

        // Assert
        viewModel.HasPendingAction.ShouldBeFalse();
        viewModel.Query.ShouldBe("бюджет", "Esc закрыл панель, а не очистил запрос");
        _actions.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_Confirmed_MovesToRecycleBinAndRemovesRow()
    {
        // Arrange
        using var viewModel = await CreateWithQueryAsync("отчёт");
        var target = viewModel.SelectedResult!;

        // Act
        await viewModel.ExecuteAsync(QuickSearchCommand.Delete);
        var asked = viewModel.IsConfirmingDelete;
        var callsBeforeConfirm = _actions.Calls.Count;
        await viewModel.ConfirmPendingActionAsync();

        // Assert
        asked.ShouldBeTrue();
        callsBeforeConfirm.ShouldBe(0, "без подтверждения в корзину ничего не уходит");
        _actions.Calls.ShouldHaveSingleItem().ShouldStartWith("Recycle: ");
        viewModel.Results.ShouldNotContain(target);
        viewModel.Rows.ShouldNotContain(target);
        viewModel.Results.Count.ShouldBe(1);
        viewModel.SelectedIndex.ShouldBe(0);
        viewModel.NoticeTitle.ShouldBe("Перемещено в корзину");
    }

    [Fact]
    public async Task Delete_QueryEdited_CancelsConfirmation()
    {
        // Arrange
        using var viewModel = await CreateWithQueryAsync("отчёт");
        await viewModel.ExecuteAsync(QuickSearchCommand.Delete);

        // Act
        viewModel.Query = "отчёт 3";
        await viewModel.PendingRefresh;

        // Assert
        viewModel.HasPendingAction.ShouldBeFalse();
        _actions.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_LastRow_HidesList()
    {
        // Arrange
        using var viewModel = await CreateWithQueryAsync("Бюджет 2026");

        // Act
        await viewModel.ExecuteAsync(QuickSearchCommand.Delete);
        await viewModel.ConfirmPendingActionAsync();

        // Assert
        viewModel.Results.ShouldBeEmpty();
        viewModel.SelectedIndex.ShouldBe(-1);
        viewModel.ShowResults.ShouldBeFalse();
    }

    [Fact]
    public async Task TogglePin_Twice_PinsAndUnpinsWithMenuText()
    {
        // Arrange
        using var viewModel = await CreateWithQueryAsync("отчёт");
        var before = viewModel.PinMenuText;

        // Act
        await viewModel.ExecuteAsync(QuickSearchCommand.TogglePin);
        var pinned = viewModel.SelectedResult!.IsFavorite;
        var afterPin = viewModel.PinMenuText;
        await viewModel.ExecuteAsync(QuickSearchCommand.TogglePin);

        // Assert
        before.ShouldBe("Закрепить");
        pinned.ShouldBeTrue();
        afterPin.ShouldBe("Открепить");
        viewModel.SelectedResult!.IsFavorite.ShouldBeFalse();
        _actions.Calls.Select(static call => call.Split(':')[0]).ShouldBe(["Pin", "Unpin"]);
    }

    [Fact]
    public async Task MoveUp_AtTopOfEmptyQuery_WalksHistory()
    {
        // Arrange
        using var viewModel = Create();
        await viewModel.PendingRefresh;

        // Act
        await viewModel.ExecuteAsync(QuickSearchCommand.MoveUp);
        await viewModel.PendingRefresh;
        var first = viewModel.Query;
        await viewModel.ExecuteAsync(QuickSearchCommand.MoveUp);
        await viewModel.PendingRefresh;

        // Assert
        first.ShouldBe("отчёт");
        viewModel.Query.ShouldBe("бюджет");
        viewModel.Results[0].Entry.Name.ShouldBe("Бюджет 2026.xlsx");
    }

    [Fact]
    public async Task MoveUp_InTypedResults_MovesSelectionOnly()
    {
        // Arrange
        using var viewModel = await CreateWithQueryAsync("pdf");
        viewModel.MoveSelection(2);

        // Act
        await viewModel.ExecuteAsync(QuickSearchCommand.MoveUp);
        await viewModel.ExecuteAsync(QuickSearchCommand.MoveUp);
        await viewModel.ExecuteAsync(QuickSearchCommand.MoveUp);

        // Assert
        viewModel.SelectedIndex.ShouldBe(0);
        viewModel.Query.ShouldBe("pdf", "введённый запрос историей не заменяется");
    }

    [Fact]
    public async Task FirstAndLast_JumpToListEnds()
    {
        // Arrange
        using var viewModel = await CreateWithQueryAsync("pdf");

        // Act
        await viewModel.ExecuteAsync(QuickSearchCommand.Last);
        var last = viewModel.SelectedIndex;
        await viewModel.ExecuteAsync(QuickSearchCommand.First);

        // Assert
        last.ShouldBe(viewModel.Results.Count - 1);
        viewModel.SelectedIndex.ShouldBe(0);
    }

    [Fact]
    public async Task Commands_WithoutSelection_DoNothing()
    {
        // Arrange
        using var viewModel = await CreateWithQueryAsync("zzzz-ничего");

        // Act
        foreach (var command in new[] { QuickSearchCommand.Reveal, QuickSearchCommand.CopyFiles, QuickSearchCommand.Rename, QuickSearchCommand.Delete, QuickSearchCommand.TogglePin })
        {
            await viewModel.ExecuteAsync(command);
        }

        // Assert
        _actions.Calls.ShouldBeEmpty();
        viewModel.HasPendingAction.ShouldBeFalse();
    }

    private QuickSearchViewModel Create()
        => new(new SampleSearchService(static () => Now), _actions, static () => Now) { DebounceDelay = TimeSpan.Zero };

    private async Task<QuickSearchViewModel> CreateWithQueryAsync(string query)
    {
        var viewModel = Create();
        viewModel.Query = query;
        await viewModel.PendingRefresh;
        return viewModel;
    }
}

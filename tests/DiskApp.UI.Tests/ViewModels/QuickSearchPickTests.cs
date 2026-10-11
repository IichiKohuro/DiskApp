using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Режим выбора папки для Quick Switch (PLAN.md, этап 8.6, ADR-0008): фильтр папок, <c>Enter</c> отдаёт папку вместо
/// открытия, <c>Esc</c> отменяет, проблемы показываются предупреждением.
/// </summary>
public sealed class QuickSearchPickTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.FromHours(3));

    private readonly SampleFileActions _actions = new();
    private readonly List<string> _picked = [];

    [Fact]
    public void BeginFolderPick_SetsFolderFilterAndHint()
    {
        // Arrange
        using var viewModel = Create();

        // Act
        viewModel.BeginFolderPick(Remember);

        // Assert
        viewModel.IsPickingFolder.ShouldBeTrue();
        viewModel.Query.ShouldBe("type:folder ");
        viewModel.IsNoticeOpen.ShouldBeTrue();
        viewModel.NoticeSeverity.ShouldBe(FAInfoBarSeverity.Informational);
        viewModel.NoticeTitle.ShouldBe("Выберите папку");
    }

    [Fact]
    public async Task OpenSelected_FolderHit_PassesFolderInsteadOfOpening()
    {
        // Arrange
        using var viewModel = await PickWithQueryAsync("diskapp");
        Select(viewModel, static hit => hit.Entry.Kind == FileKind.Folder);
        var folder = viewModel.SelectedResult!.Entry.FullPath;
        var dismissed = 0;
        viewModel.DismissRequested += (_, _) => dismissed++;
        var opened = 0;
        viewModel.FileOpened += (_, _) => opened++;

        // Act
        var handled = await viewModel.OpenSelectedAsync();

        // Assert
        handled.ShouldBeTrue();
        _picked.ShouldBe([folder]);
        opened.ShouldBe(0, "в режиме выбора ничего не открывается");
        dismissed.ShouldBe(1);
        viewModel.IsPickingFolder.ShouldBeFalse();
        viewModel.IsNoticeOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task OpenSelected_FileHit_PassesContainingFolder()
    {
        // Arrange
        using var viewModel = await PickWithQueryAsync("plan");
        Select(viewModel, static hit => hit.Entry.Kind != FileKind.Folder);
        var entry = viewModel.SelectedResult!.Entry;
        var opened = 0;
        viewModel.FileOpened += (_, _) => opened++;

        // Act
        await viewModel.OpenSelectedAsync();

        // Assert
        _picked.ShouldBe([entry.Directory]);
        opened.ShouldBe(0);
    }

    [Fact]
    public async Task Escape_WhilePicking_CancelsAndClosesWithoutCallback()
    {
        // Arrange
        using var viewModel = await PickWithQueryAsync("diskapp");
        var closed = 0;
        viewModel.CloseRequested += (_, _) => closed++;

        // Act
        viewModel.Escape();

        // Assert
        closed.ShouldBe(1, "одно нажатие Esc сразу возвращает в диалог");
        _picked.ShouldBeEmpty();
        viewModel.IsPickingFolder.ShouldBeFalse();
        viewModel.Query.ShouldBeEmpty();
    }

    [Fact]
    public async Task OpenSelected_AfterCancel_OpensNormally()
    {
        // Arrange
        using var viewModel = Create();
        viewModel.BeginFolderPick(Remember);
        viewModel.CancelFolderPick();
        viewModel.Query = "бюджет";
        await viewModel.PendingRefresh;
        var opened = new List<string>();
        viewModel.FileOpened += (_, entry) => opened.Add(entry.Name);

        // Act
        await viewModel.OpenSelectedAsync();

        // Assert
        _picked.ShouldBeEmpty();
        opened.ShouldHaveSingleItem().ShouldStartWith("Бюджет");
    }

    [Fact]
    public void ReportFolderPickProblem_ShowsWarning()
    {
        // Arrange
        using var viewModel = Create();

        // Act
        viewModel.ReportFolderPickProblem("Диалог уже закрыт", "Окно закрылось.");

        // Assert
        viewModel.IsNoticeOpen.ShouldBeTrue();
        viewModel.NoticeSeverity.ShouldBe(FAInfoBarSeverity.Warning);
        viewModel.NoticeTitle.ShouldBe("Диалог уже закрыт");
        viewModel.NoticeMessage.ShouldBe("Окно закрылось.");
    }

    private static void Select(QuickSearchViewModel viewModel, Func<SearchHit, bool> predicate)
    {
        var index = viewModel.Results.ToList().FindIndex(hit => predicate(hit));
        index.ShouldBeGreaterThanOrEqualTo(0, "в образцах есть подходящая строка");
        viewModel.SelectedIndex = index;
    }

    private QuickSearchViewModel Create()
        => new(new SampleSearchService(static () => Now), _actions, static () => Now) { DebounceDelay = TimeSpan.Zero };

    private async Task<QuickSearchViewModel> PickWithQueryAsync(string query)
    {
        var viewModel = Create();
        viewModel.BeginFolderPick(Remember);
        viewModel.Query = query;
        await viewModel.PendingRefresh;
        return viewModel;
    }

    private Task Remember(string folder)
    {
        _picked.Add(folder);
        return Task.CompletedTask;
    }
}

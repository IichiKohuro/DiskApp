using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;

using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;
using DiskApp.UI.Views;

namespace DiskApp.UI.Tests.Views;

/// <summary>
/// Клавиатура быстрого поиска на headless-платформе (PLAN.md, этапы 7.2 и 7.7): клавиши из поля запроса доходят до
/// команд, панель переименования получает фокус, контекстное меню открывается с клавиатуры.
/// </summary>
public sealed class QuickSearchViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 15, 0, 0, TimeSpan.FromHours(3));

    [Fact]
    public Task F2ThenEnter_RenamesSelectedFile() => HeadlessTest.RunAsync(static async () =>
    {
        // Arrange
        var actions = new SampleFileActions();
        var (window, view, viewModel) = await ShowAsync(actions, "бюджет");
        var renameBox = view.FindControl<TextBox>("RenameBox")!;

        // Act
        window.KeyPress(Key.F2, RawInputModifiers.None, PhysicalKey.F2, null);
        Dispatcher.UIThread.RunJobs();
        var focused = renameBox.IsFocused;
        var selected = renameBox.SelectedText;
        viewModel.RenameText = "Бюджет 2027.xlsx";
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();

        // Assert
        focused.ShouldBeTrue();
        selected.ShouldBe("Бюджет 2026", "выделено имя без расширения");
        actions.Calls.ShouldHaveSingleItem().ShouldEndWith(" → Бюджет 2027.xlsx");
        viewModel.HasPendingAction.ShouldBeFalse();
        window.Close();
        viewModel.Dispose();
    });

    [Fact]
    public Task DeleteThenEnter_MovesToRecycleBinAfterConfirmation() => HeadlessTest.RunAsync(static async () =>
    {
        // Arrange
        var actions = new SampleFileActions();
        var (window, _, viewModel) = await ShowAsync(actions, "бюджет");

        // Act
        window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
        var confirming = viewModel.IsConfirmingDelete;
        var callsBefore = actions.Calls.Count;
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();

        // Assert
        confirming.ShouldBeTrue();
        callsBefore.ShouldBe(0);
        actions.Calls.ShouldHaveSingleItem().ShouldStartWith("Recycle: ");
        viewModel.Query.ShouldBe("бюджет", "Del в конце поля не стёр запрос");
        window.Close();
        viewModel.Dispose();
    });

    [Fact]
    public Task CtrlShiftC_CopiesPath() => HeadlessTest.RunAsync(static async () =>
    {
        // Arrange
        var actions = new SampleFileActions();
        var (window, _, viewModel) = await ShowAsync(actions, "бюджет");

        // Act
        window.KeyPress(Key.C, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.C, null);
        Dispatcher.UIThread.RunJobs();

        // Assert
        actions.Calls.ShouldHaveSingleItem().ShouldStartWith("CopyPaths: ");
        window.Close();
        viewModel.Dispose();
    });

    [Fact]
    public Task Tab_OpensContextMenuForSelectedRow() => HeadlessTest.RunAsync(static async () =>
    {
        // Arrange
        var (window, view, viewModel) = await ShowAsync(new SampleFileActions(), "бюджет");
        var list = view.FindControl<ListBox>("ResultsList")!;
        var menu = (MenuFlyout)list.ContextFlyout!;

        // Act
        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        Dispatcher.UIThread.RunJobs();

        // Assert
        menu.IsOpen.ShouldBeTrue();
        menu.Items.OfType<MenuItem>().Count().ShouldBe(10);
        menu.Hide();
        window.Close();
        viewModel.Dispose();
    });

    private static async Task<(Window Window, QuickSearchView View, QuickSearchViewModel ViewModel)> ShowAsync(SampleFileActions actions, string query)
    {
        var viewModel = new QuickSearchViewModel(new SampleSearchService(static () => Now), actions, static () => Now) { DebounceDelay = TimeSpan.Zero };
        var view = new QuickSearchView { DataContext = viewModel };
        var window = new Window { Width = 720, Height = 500, Content = view };
        window.Show();
        viewModel.Query = query;
        await viewModel.PendingRefresh;
        Dispatcher.UIThread.RunJobs();
        view.FocusQuery();
        view.FindControl<TextBox>("QueryBox")!.CaretIndex = query.Length;
        return (window, view, viewModel);
    }
}

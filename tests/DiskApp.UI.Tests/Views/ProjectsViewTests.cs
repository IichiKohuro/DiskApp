using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

using DiskApp.UI.Controls;
using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;
using DiskApp.UI.Views;

namespace DiskApp.UI.Tests.Views;

/// <summary>
/// Страница «Проекты» на headless-платформе (PLAN.md, этап 24.3): в списке и карточке — иконки с номером, в карточке —
/// номер, название и папка проекта.
/// </summary>
public sealed class ProjectsViewTests
{
    [Fact]
    public Task Page_ShowsBadgesNumberTitleAndFolder() => HeadlessTest.RunAsync(static async () =>
    {
        // Arrange
        var projects = new SampleProjectsService(new ProjectInput("10S-417", "Школа", null, @"D:\Work\10S-417"), new ProjectInput("2", string.Empty, "#0078D4", null));
        var page = new ProjectsViewModel(projects, new SampleNotesService(static () => DateTimeOffset.Now));
        page.OnNavigatedTo();
        await page.PendingLoad;
        var window = new Window { Width = 1000, Height = 700, Content = new ProjectsView { DataContext = page } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Act
        var badges = window.GetVisualDescendants().OfType<ProjectBadge>().Where(static badge => badge.IsEffectivelyVisible).Select(static badge => badge.Number).ToList();
        var boxes = window.GetVisualDescendants().OfType<TextBox>().Select(static box => box.Text).ToList();
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(static block => block.IsEffectivelyVisible).Select(static block => block.Text).ToList();

        // Assert: «2» раньше «10S-417»; открыт первый
        badges.ShouldContain("2");
        badges.ShouldContain("10S-417");
        boxes.ShouldContain("2");
        texts.ShouldContain("Папка не выбрана");
        page.SelectedProject = page.Projects[1];
        Dispatcher.UIThread.RunJobs();
        window.GetVisualDescendants().OfType<TextBox>().Select(static box => box.Text).ShouldContain("Школа");
        window.GetVisualDescendants().OfType<TextBlock>().Select(static block => block.Text).ShouldContain(@"D:\Work\10S-417");
        window.Close();
    });
}

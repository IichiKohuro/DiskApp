using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;
using DiskApp.UI.Views;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.Tests.Views;

/// <summary>
/// Тихая карточка уведомления на headless-платформе (PLAN.md, этап 21.1): заголовок, текст и кнопки на месте, кнопка
/// действия выполняет его и закрывает карточку.
/// </summary>
public sealed class NotificationCardViewTests
{
    [Fact]
    public Task Card_ShowsTextAndRunsAction() => HeadlessTest.RunAsync(static () =>
    {
        // Arrange
        var undone = false;
        var card = new NotificationCardViewModel(new Notification(NotificationKind.AutoSort, "Авторазбор", "«акт.pdf» → 417 › Акты")
        {
            Severity = FAInfoBarSeverity.Success,
            Actions = [new NotificationAction("Отменить", () => undone = true)],
        });
        var closed = false;
        card.CloseRequested += (_, _) => closed = true;
        var window = new Window { Width = 360, Height = 160, Content = new NotificationCardView { DataContext = card } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Act
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(static block => block.Text).Where(static text => text is { Length: > 0 }).ToList();
        var undo = window.GetVisualDescendants().OfType<Button>().Single(static button => Equals(button.Content, "Отменить"));
        undo.Command!.Execute(null);

        // Assert
        texts.ShouldContain("Авторазбор");
        texts.ShouldContain("«акт.pdf» → 417 › Акты");
        undone.ShouldBeTrue();
        closed.ShouldBeTrue();
        window.Close();
        return Task.CompletedTask;
    });
}

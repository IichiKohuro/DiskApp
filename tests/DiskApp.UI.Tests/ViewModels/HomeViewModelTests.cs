using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Главная страница: приветствие по времени суток, загрузка состояния и недавних, быстрые действия.
/// </summary>
public sealed class HomeViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 15, 0, 0, TimeSpan.FromHours(3));

    [Theory]
    [InlineData(5, "Доброе утро")]
    [InlineData(11, "Доброе утро")]
    [InlineData(12, "Добрый день")]
    [InlineData(17, "Добрый день")]
    [InlineData(18, "Добрый вечер")]
    [InlineData(22, "Добрый вечер")]
    [InlineData(23, "Доброй ночи")]
    [InlineData(3, "Доброй ночи")]
    public void GreetingFor_Hour_MatchesTimeOfDay(int hour, string expected)
    {
        // Act
        var greeting = HomeViewModel.GreetingFor(hour);

        // Assert
        greeting.ShouldBe(expected);
    }

    [Fact]
    public async Task Initialization_WithSampleIndex_LoadsStatusRecentAndInbox()
    {
        // Arrange
        var viewModel = Create(out _);

        // Act
        await viewModel.Initialization;

        // Assert
        viewModel.Greeting.ShouldBe("Добрый день");
        viewModel.IndexStatusText.ShouldContain("файлов в 4 корнях");
        viewModel.RecentFiles.Count.ShouldBe(6);
        viewModel.InboxCount.ShouldBe(12);
        viewModel.HasInbox.ShouldBeTrue();
        viewModel.InboxText.ShouldBe("12 файлов ждут разбора в «Загрузках»");
    }

    [Fact]
    public async Task QuickActions_Executed_CallShellCommands()
    {
        // Arrange
        var viewModel = Create(out var commands);
        await viewModel.Initialization;

        // Act
        viewModel.OpenQuickSearchCommand.Execute(null);
        viewModel.OpenInboxCommand.Execute(null);
        viewModel.OpenSettingsCommand.Execute(null);

        // Assert
        commands.Received(1).ShowQuickSearch();
        commands.Received(1).NavigateTo(PageKeys.Inbox);
        commands.Received(1).NavigateTo(PageKeys.Settings);
    }

    [Fact]
    public async Task OpenFileCommand_WithRecentFile_PromotesItAndReportsOpening()
    {
        // Arrange
        var viewModel = Create(out _);
        await viewModel.Initialization;
        var hit = viewModel.RecentFiles[3];

        // Act
        await viewModel.OpenFileCommand.ExecuteAsync(hit);

        // Assert
        viewModel.RecentFiles[0].Entry.FullPath.ShouldBe(hit.Entry.FullPath);
        viewModel.LastOpenedText.ShouldContain(hit.Entry.Name);
    }

    private static HomeViewModel Create(out IAppCommands commands)
    {
        commands = Substitute.For<IAppCommands>();
        return new HomeViewModel(new SampleSearchService(static () => Now), commands, new HotkeyStatus(), static () => Now);
    }
}

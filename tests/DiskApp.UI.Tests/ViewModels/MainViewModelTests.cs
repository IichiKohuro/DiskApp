using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Оболочка главного окна: состав и порядок страниц, навигация по ключу, уведомление, состояние индекса.
/// </summary>
public sealed class MainViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 15, 0, 0, TimeSpan.FromHours(3));

    [Fact]
    public void Constructor_Always_ListsPagesInNavigationOrder()
    {
        // Arrange & Act
        var viewModel = Create();

        // Assert
        viewModel.Pages.Select(page => page.Key).ShouldBe(
            [PageKeys.Home, PageKeys.Search, PageKeys.Browse, PageKeys.Collections, PageKeys.Tags, PageKeys.Inbox, PageKeys.Operations]);
        viewModel.FooterPages.Select(page => page.Key).ShouldBe([PageKeys.Settings]);
        viewModel.SelectedPage.ShouldBeOfType<HomeViewModel>();
    }

    [Fact]
    public void NavigateTo_KnownKey_SelectsPage()
    {
        // Arrange
        var viewModel = Create();

        // Act
        var found = viewModel.NavigateTo(PageKeys.Settings);

        // Assert
        found.ShouldBeTrue();
        viewModel.SelectedPage.ShouldBeOfType<SettingsViewModel>();
    }

    [Fact]
    public void NavigateTo_UnknownKey_KeepsCurrentPage()
    {
        // Arrange
        var viewModel = Create();

        // Act
        var found = viewModel.NavigateTo("no-such-page");

        // Assert
        found.ShouldBeFalse();
        viewModel.SelectedPage.ShouldBeOfType<HomeViewModel>();
    }

    [Fact]
    public async Task Initialization_WithSampleIndex_FillsStatusAndInboxBadge()
    {
        // Arrange
        var viewModel = Create();

        // Act
        await viewModel.Initialization;

        // Assert
        viewModel.IsIndexing.ShouldBeFalse();
        viewModel.IndexStatusShort.ShouldBe("Индекс актуален");
        viewModel.IndexStatusText.ShouldBe("128 412 файлов в 4 корнях · обновлён 2 минуты назад");
        var inbox = viewModel.Pages.Single(page => page.Key == PageKeys.Inbox);
        inbox.BadgeValue.ShouldBe(12);
        inbox.HasBadge.ShouldBeTrue();
    }

    [Fact]
    public void ShowNotice_Always_OpensInfoBarWithSeverity()
    {
        // Arrange
        var viewModel = Create();

        // Act
        viewModel.ShowNotice("Заголовок", "Текст", FAInfoBarSeverity.Warning);

        // Assert
        viewModel.IsNoticeOpen.ShouldBeTrue();
        viewModel.NoticeTitle.ShouldBe("Заголовок");
        viewModel.NoticeMessage.ShouldBe("Текст");
        viewModel.NoticeSeverity.ShouldBe(FAInfoBarSeverity.Warning);
    }

    private static MainViewModel Create()
    {
        var search = new SampleSearchService(static () => Now);
        var commands = Substitute.For<IAppCommands>();
        var themes = Substitute.For<IThemeService>();

        return new MainViewModel(
            new HomeViewModel(search, commands, static () => Now),
            new QuickSearchViewModel(search, static () => Now),
            new SettingsViewModel(themes, search),
            search,
            static () => Now);
    }
}

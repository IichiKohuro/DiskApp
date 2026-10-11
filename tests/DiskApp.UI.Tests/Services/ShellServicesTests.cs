using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.Tests.Services;

/// <summary>
/// Сервисы оболочки поверх главной ViewModel (PLAN.md, этап 6.1): навигация, уведомления, ленивое создание модели.
/// </summary>
public sealed class ShellServicesTests
{
    [Fact]
    public void Navigation_KnownAndUnknownPages()
    {
        // Arrange
        var created = 0;
        var main = new Lazy<MainViewModel>(() =>
        {
            created++;
            return Create();
        });
        var navigation = new NavigationService(main);

        // Act
        var beforeUse = created;
        var settings = navigation.NavigateTo(PageKeys.Settings);
        var unknown = navigation.NavigateTo("nowhere");

        // Assert
        beforeUse.ShouldBe(0, "главная модель создаётся при первом обращении");
        settings.ShouldBeTrue();
        unknown.ShouldBeFalse();
        navigation.CurrentPage.ShouldBe(PageKeys.Settings);
    }

    [Fact]
    public void InfoBar_ShowAndClose()
    {
        // Arrange
        var main = Create();
        var notices = new InfoBarService(new Lazy<MainViewModel>(main));

        // Act
        notices.Show("Сервер не запустился", "Порт занят", FAInfoBarSeverity.Error);
        var shown = (main.IsNoticeOpen, main.NoticeTitle, main.NoticeMessage, main.NoticeSeverity);
        notices.Close();

        // Assert
        shown.ShouldBe((true, "Сервер не запустился", "Порт занят", FAInfoBarSeverity.Error));
        main.IsNoticeOpen.ShouldBeFalse();
    }

    private static MainViewModel Create()
    {
        var now = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.FromHours(3));
        var search = new SampleSearchService(() => now);
        var hotkeys = new HotkeyStatus();
        return new MainViewModel(
            new HomeViewModel(search, Substitute.For<IAppCommands>(), hotkeys, () => now),
            new QuickSearchViewModel(search, () => now),
            new SettingsViewModel(Substitute.For<IThemeService>(), search, hotkeys),
            search,
            () => now);
    }
}

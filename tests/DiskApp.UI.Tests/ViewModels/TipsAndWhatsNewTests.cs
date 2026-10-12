using DiskApp.UI.Localization;
using DiskApp.UI.Services;
using DiskApp.UI.Tests.Localization;
using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Обучающие подсказки и «Что нового» (PLAN.md, этап 9.3): каждая подсказка — один раз и только после мастера,
/// подсказки главной идут по очереди, пункты «Что нового» берутся из ресурсов на текущем языке.
/// </summary>
[Collection(LanguageTests.Name)]
public sealed class TipsAndWhatsNewTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.FromHours(3));

    [Fact]
    public async Task Tips_BeforeOnboarding_NotShown()
    {
        // Arrange
        var tips = new SettingsTipsService(new SampleSettingsService());

        // Act
        await tips.LoadAsync();

        // Assert
        tips.ShouldShow(Tips.Hotkey).ShouldBeFalse("мастер ещё не пройден");
        tips.Enable();
        tips.ShouldShow(Tips.Hotkey).ShouldBeTrue();
    }

    [Fact]
    public async Task Tips_MarkShown_SavedOnceAndNotShownAgain()
    {
        // Arrange
        var settings = Completed();
        var tips = new SettingsTipsService(settings);
        await tips.LoadAsync();

        // Act
        tips.MarkShown(Tips.Hotkey);
        tips.MarkShown(Tips.Drop);
        tips.MarkShown(Tips.Hotkey);

        // Assert
        tips.ShouldShow(Tips.Hotkey).ShouldBeFalse();
        tips.ShouldShow(Tips.Filters).ShouldBeTrue();
        settings.PatchCount.ShouldBe(2, "повторная отметка не сохраняется");
        settings.LastPatch!.ToJsonString().ShouldBe("""{"onboarding":{"tipsShown":["drop","hotkey"]}}""");

        var reloaded = new SettingsTipsService(settings);
        await reloaded.LoadAsync();
        reloaded.ShouldShow(Tips.Drop).ShouldBeFalse("показанные подсказки переживают перезапуск");
    }

    [Fact]
    public async Task Tips_Reset_ShownAgainAfterWizard()
    {
        // Arrange
        var settings = Completed();
        var tips = new SettingsTipsService(settings);
        await tips.LoadAsync();
        tips.MarkShown(Tips.Hotkey);

        // Act
        tips.Reset();
        var duringWizard = tips.ShouldShow(Tips.Hotkey);
        tips.Enable();

        // Assert
        duringWizard.ShouldBeFalse("пока идёт мастер, подсказки не показываются");
        tips.ShouldShow(Tips.Hotkey).ShouldBeTrue();
        settings.LastPatch!.ToJsonString().ShouldBe("""{"onboarding":{"tipsShown":[]}}""");
    }

    [Fact]
    public void Settings_RestartOnboarding_AsksShell()
    {
        // Arrange
        var commands = Substitute.For<IAppCommands>();
        var page = new SettingsViewModel(Substitute.For<IThemeService>(), new SampleSearchService(static () => Now), new HotkeyStatus(), commands: commands);

        // Act
        page.RestartOnboardingCommand.Execute(null);

        // Assert
        commands.Received(1).StartOnboarding();
    }

    [Fact]
    public async Task Tips_SaveRejected_DoesNotThrow()
    {
        // Arrange
        var settings = Completed();
        var tips = new SettingsTipsService(settings);
        await tips.LoadAsync();
        settings.RejectWith = "сервер недоступен";

        // Act & Assert
        Should.NotThrow(() => tips.MarkShown(Tips.Filters));
        tips.ShouldShow(Tips.Filters).ShouldBeFalse();
    }

    [Fact]
    public async Task Home_ShowTips_HotkeyThenDropThenNothing()
    {
        // Arrange
        var tips = new SettingsTipsService(Completed());
        await tips.LoadAsync();
        var home = CreateHome(tips);

        // Act & Assert
        home.ShowTips();
        home.IsHotkeyTipOpen.ShouldBeTrue();
        home.IsDropTipOpen.ShouldBeFalse("по одной подсказке за раз");
        home.HotkeyTipText.ShouldStartWith(HotkeyStatus.DefaultQuickSearchText);

        home.IsHotkeyTipOpen = false;
        home.IsDropTipOpen.ShouldBeTrue("после горячей клавиши — перетаскивание");
        tips.ShouldShow(Tips.Hotkey).ShouldBeFalse();

        home.IsDropTipOpen = false;
        tips.ShouldShow(Tips.Drop).ShouldBeFalse();

        home.ShowTips();
        home.IsHotkeyTipOpen.ShouldBeFalse();
        home.IsDropTipOpen.ShouldBeFalse();
    }

    [Fact]
    public void Home_WithoutTipsService_ShowsNothing()
    {
        // Arrange
        var home = new HomeViewModel(new SampleSearchService(static () => Now), Substitute.For<IAppCommands>(), new HotkeyStatus(), static () => Now);

        // Act
        home.ShowTips();

        // Assert
        home.IsHotkeyTipOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task QuickSearch_ShowTips_FiltersOnce()
    {
        // Arrange
        var tips = new SettingsTipsService(Completed());
        await tips.LoadAsync();
        using var search = new QuickSearchViewModel(new SampleSearchService(static () => Now), new SampleFileActions(), null, tips);

        // Act & Assert
        search.ShowTips();
        search.IsFiltersTipOpen.ShouldBeTrue();
        tips.ShouldShow(Tips.Filters).ShouldBeFalse("показана — больше не показывать, даже если окно спрячут");
        search.DismissFiltersTipCommand.Execute(null);
        search.IsFiltersTipOpen.ShouldBeFalse();
        search.ShowTips();
        search.IsFiltersTipOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task QuickSearch_Typing_ClosesFiltersTip()
    {
        // Arrange
        var tips = new SettingsTipsService(Completed());
        await tips.LoadAsync();
        using var search = new QuickSearchViewModel(new SampleSearchService(static () => Now), new SampleFileActions(), null, tips);
        search.ShowTips();

        // Act
        search.Query = "отчёт";

        // Assert
        search.IsFiltersTipOpen.ShouldBeFalse();
    }

    [Fact]
    public void WhatsNew_ListsItemsFromResources()
    {
        // Act
        var whatsNew = new WhatsNewViewModel();

        // Assert
        whatsNew.Version.ShouldBe(AppVersion.Current);
        whatsNew.Title.ShouldBe($"Что нового в DiskApp {AppVersion.Current}");
        whatsNew.Items.Count.ShouldBeGreaterThanOrEqualTo(5);
        whatsNew.Items.ShouldAllBe(static item => item.Title.Length > 0 && item.Text.Length > 0 && !item.Text.StartsWith("WhatsNew.", StringComparison.Ordinal));
    }

    [Fact]
    public void WhatsNew_InEnglish_Translated()
    {
        // Arrange
        Localizer.Instance.SetLanguage("en");
        try
        {
            // Act
            var whatsNew = new WhatsNewViewModel();

            // Assert
            whatsNew.Title.ShouldBe($"What's new in DiskApp {AppVersion.Current}");
            whatsNew.Items[0].Title.ShouldBe("Notes");
        }
        finally
        {
            Localizer.Instance.SetLanguage("ru");
        }
    }

    [Fact]
    public async Task Main_StartOnboarding_ShowsWizardUntilCompleted()
    {
        // Arrange
        var search = new SampleSearchService(static () => Now);
        var main = new MainViewModel(
            new HomeViewModel(search, Substitute.For<IAppCommands>(), new HotkeyStatus(), static () => Now),
            new QuickSearchViewModel(search, static () => Now),
            new SettingsViewModel(Substitute.For<IThemeService>(), search, new HotkeyStatus()),
            search,
            static () => Now);
        var roots = Substitute.For<IRootsService>();
        roots.GetRootsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<DiskApp.UI.Models.RootInfo>>([]));
        var settings = new SampleSettingsService();
        var wizard = new OnboardingViewModel(roots, settings, new SampleSearchService(static () => Now), Substitute.For<IThemeService>(), new HotkeyStatus(), suggestions: []);
        main.NavigateTo(PageKeys.Settings);

        // Act
        main.StartOnboarding(wizard);
        var shown = main.IsOnboarding;
        await wizard.SkipCommand.ExecuteAsync(null);

        // Assert
        shown.ShouldBeTrue();
        main.IsOnboarding.ShouldBeFalse();
        main.Onboarding.ShouldBeNull();
        main.SelectedPage!.Key.ShouldBe(PageKeys.Home);
    }

    private static SampleSettingsService Completed()
    {
        var settings = SampleSettingsService.Defaults();
        settings.Onboarding.Completed = true;
        return new SampleSettingsService(settings);
    }

    private static HomeViewModel CreateHome(ITipsService tips)
        => new(new SampleSearchService(static () => Now), Substitute.For<IAppCommands>(), new HotkeyStatus(), new SampleRootsService(), Substitute.For<IInfoBarService>(), tips);
}

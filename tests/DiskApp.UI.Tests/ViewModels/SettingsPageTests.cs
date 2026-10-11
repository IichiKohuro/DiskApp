using System.Text.Json.Nodes;

using DiskApp.Contracts.App;
using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Страница настроек (PLAN.md, этап 9.1): загрузка без сохранения, сохранение каждого изменения частичным PATCH, откат
/// отклонённого значения, правила исключения, корни, «Входящие», разделы и карта клавиш.
/// </summary>
public sealed class SettingsPageTests
{
    private readonly IThemeService _themes = Substitute.For<IThemeService>();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly IInfoBarService _notices = Substitute.For<IInfoBarService>();
    private readonly SampleRootsService _roots = new();

    [Fact]
    public async Task Load_ShowsSavedValuesWithoutSaving()
    {
        // Arrange
        var saved = SampleSettingsService.Defaults();
        saved.Search.Fuzzy = false;
        saved.Popup.Monitor = PopupMonitor.Primary;
        saved.Popup.ShowSize = false;
        saved.Appearance.Compact = true;
        saved.Appearance.UseSystemAccent = false;
        saved.Appearance.AccentColor = "#E81123";
        saved.Appearance.FontScale = 1.25;
        saved.Appearance.Backdrop = BackdropMode.Acrylic;
        saved.Diagnostics.LogLevel = "Warning";
        var service = new SampleSettingsService(saved);

        // Act
        var page = Create(service);
        await page.Initialization;

        // Assert
        page.Fuzzy.ShouldBeFalse();
        page.SelectedPopupMonitor.Value.ShouldBe(PopupMonitor.Primary);
        page.ShowSize.ShouldBeFalse();
        page.ShowPath.ShouldBeTrue();
        page.Compact.ShouldBeTrue();
        page.UseSystemAccent.ShouldBeFalse();
        page.SelectedAccentColor.ShouldBe("#E81123");
        page.SelectedFontScale.Name.ShouldBe("125 %");
        page.SelectedBackdrop.Value.ShouldBe(BackdropMode.Acrylic);
        page.SelectedLogLevel.Value.ShouldBe("Warning");
        service.PatchCount.ShouldBe(0, "загрузка ничего не сохраняет");
    }

    [Fact]
    public async Task Toggle_SavesOnlyThatField()
    {
        // Arrange
        var service = new SampleSettingsService();
        var page = Create(service);
        await page.Initialization;

        // Act
        page.Fuzzy = false;
        await page.PendingSave;
        page.SelectedPopupMonitor = page.PopupMonitors.Single(option => option.Value == PopupMonitor.ActiveWindow);
        await page.PendingSave;

        // Assert
        service.PatchCount.ShouldBe(2);
        service.LastPatch!.ToJsonString().ShouldBe("""{"popup":{"monitor":"ActiveWindow"}}""");
        (await service.GetAsync(TestContext.Current.CancellationToken)).Search.Fuzzy.ShouldBeFalse();
    }

    [Fact]
    public async Task Theme_ChangedByUser_AppliesAndSaves()
    {
        // Arrange
        var service = new SampleSettingsService();
        var page = Create(service);
        await page.Initialization;

        // Act
        page.SelectedTheme = page.Themes.Single(option => option.Theme == AppTheme.Dark);
        await page.PendingSave;

        // Assert
        _themes.Received(1).Apply(AppTheme.Dark);
        (await service.GetAsync(TestContext.Current.CancellationToken)).Appearance.Theme.ShouldBe(ThemeMode.Dark);
    }

    [Fact]
    public async Task Rejected_ShowsReasonAndRevertsValue()
    {
        // Arrange
        var service = new SampleSettingsService();
        var page = Create(service);
        await page.Initialization;
        service.RejectWith = "Допустимо от 100 до 5000.";

        // Act
        page.CandidateLimit = 9_000;
        await page.PendingSave;

        // Assert
        page.SettingsNote.ShouldBe("Настройка не сохранена: Допустимо от 100 до 5000.");
        page.CandidateLimit.ShouldBe(2_000);
    }

    [Fact]
    public async Task Exclusions_AddValidateRemoveAndReset()
    {
        // Arrange
        var service = new SampleSettingsService();
        var page = Create(service);
        await page.Initialization;
        var initial = page.Exclusions.Count;

        // Act & Assert: неверный шаблон не сохраняется.
        page.NewExclusion = "logs|old";
        page.AddExclusionCommand.Execute(null);
        page.HasExclusionError.ShouldBeTrue();
        service.PatchCount.ShouldBe(0);

        // Обратные косые черты становятся прямыми, правило сохраняется целым списком.
        page.NewExclusion = @"**\bin\**";
        page.AddExclusionCommand.Execute(null);
        await page.PendingSave;
        page.Exclusions.Count.ShouldBe(initial + 1);
        page.Exclusions[^1].Pattern.ShouldBe("**/bin/**");
        page.HasExclusionError.ShouldBeFalse();
        var saved = (await service.GetAsync(TestContext.Current.CancellationToken)).Indexing.Exclusions;
        saved[^1].ShouldBe(new ExclusionRuleDto(ExclusionKind.Glob, "**/bin/**"));

        // Повтор отклоняется.
        page.NewExclusion = "**/BIN/**";
        page.AddExclusionCommand.Execute(null);
        page.ExclusionError.ShouldBe("Такое правило уже есть.");

        // Удаление.
        page.RemoveExclusionCommand.Execute(page.Exclusions[^1]);
        await page.PendingSave;
        page.Exclusions.Count.ShouldBe(initial);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("**/node_modules/**", true)]
    [InlineData("*.tmp", true)]
    [InlineData("a<b", false)]
    [InlineData("***/x", false)]
    public void ValidateGlob_Cases(string pattern, bool valid)
        => (SettingsViewModel.ValidateGlob(pattern) is null).ShouldBe(valid);

    [Fact]
    public async Task Roots_FlagsDepthAddAndRemove()
    {
        // Arrange
        var page = Create(new SampleSettingsService());
        await page.Initialization;
        var item = page.RootItems[0];

        // Act: флаги и глубина сохраняются в корне.
        item.IncludeHidden = true;
        item.DepthText = "5";
        await Task.Yield();

        // Assert
        _roots.Calls.ShouldContain("update:1");
        (await _roots.GetRootsAsync(TestContext.Current.CancellationToken))[0].ShouldSatisfyAllConditions(
            root => root.IncludeHidden.ShouldBeTrue(),
            root => root.MaxDepth.ShouldBe(5));

        // Неверная глубина не уходит на сервер.
        _roots.Calls.Clear();
        item.DepthText = "много";
        page.SettingsNote.ShouldStartWith("Глубина");
        _roots.Calls.ShouldBeEmpty();

        // Добавить диск и убрать корень после подтверждения.
        await page.AddDriveCommand.ExecuteAsync(@"Z:\");
        _roots.Added.ShouldContain(@"Z:\");
        _dialogs.ConfirmAsync("Убрать из индекса", Arg.Any<string>(), "Убрать").Returns(true);
        await page.RemoveRootCommand.ExecuteAsync(page.RootItems.Single(root => root.Root.Path == @"Z:\"));
        page.RootItems.ShouldNotContain(root => root.Root.Path == @"Z:\");
        page.Roots.ShouldNotContain(@"Z:\");
    }

    [Fact]
    public async Task Inbox_PickAndReset()
    {
        // Arrange
        var service = new SampleSettingsService();
        var page = Create(service);
        await page.Initialization;
        _dialogs.PickFolderAsync(Arg.Any<string>()).Returns(@"D:\Входящие");

        // Act
        await page.PickInboxCommand.ExecuteAsync(null);
        await page.PendingSave;
        var picked = (await service.GetAsync(TestContext.Current.CancellationToken)).Add.InboxPath;
        page.ResetInboxCommand.Execute(null);
        await page.PendingSave;

        // Assert
        picked.ShouldBe(@"D:\Входящие");
        page.InboxText.ShouldStartWith("Первый корень");
        (await service.GetAsync(TestContext.Current.CancellationToken)).Add.InboxPath.ShouldBeNull();
    }

    [Fact]
    public async Task ClearHistory_Confirmed_ClearsServerHistory()
    {
        // Arrange
        var search = Substitute.For<ISearchService>();
        var page = new SettingsViewModel(_themes, search, new HotkeyStatus(), settings: new SampleSettingsService(), dialogs: _dialogs, notices: _notices, roots: _roots);
        await page.Initialization;
        _dialogs.ConfirmAsync("Очистить историю", Arg.Any<string>(), "Очистить").Returns(true);

        // Act
        await page.ClearHistoryCommand.ExecuteAsync(null);

        // Assert
        await search.Received(1).ClearHistoryAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Categories_AndKeyMap_AreComplete()
    {
        // Act
        var page = Create(new SampleSettingsService());

        // Assert
        page.Categories.Select(static category => category.Key).ShouldBe(["appearance", "quick", "search", "index", "behavior", "integrations", "inbox", "data", "about"]);
        page.SelectedCategory.Key.ShouldBe("appearance");
        page.KeyMap.ShouldContain(new KeyMapEntry("Ctrl+D", "Закрепить или открепить"));
        page.KeyMap.ShouldContain(new KeyMapEntry("Ctrl+T", "Теги"));
    }

    [Fact]
    public async Task SampleService_MergesNullAsValue()
    {
        // Arrange
        var service = new SampleSettingsService();

        // Act
        var saved = await service.PatchAsync(new JsonObject { ["appearance"] = new JsonObject { ["accentColor"] = "#107C10" } }, TestContext.Current.CancellationToken);

        // Assert
        saved.Appearance.AccentColor.ShouldBe("#107C10");
        saved.Search.PageSize.ShouldBe(50);
    }

    private SettingsViewModel Create(ISettingsService settings)
        => new(_themes, new SampleSearchService(), new HotkeyStatus(), settings: settings, dialogs: _dialogs, notices: _notices, roots: _roots);
}

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
    public async Task NewExclusion_ShowsHowManyEntriesItRemoves()
    {
        // Arrange
        var service = new SampleSettingsService
        {
            Preview = static pattern => pattern switch
            {
                "**/bin/**" => new DiskApp.Contracts.Roots.ExclusionPreviewDto(pattern, 1_234, 50_000, Complete: true),
                "*.tmp" => new DiskApp.Contracts.Roots.ExclusionPreviewDto(pattern, 0, 50_000, Complete: true),
                _ => new DiskApp.Contracts.Roots.ExclusionPreviewDto(pattern, 7, 10_000, Complete: false),
            },
        };
        var page = Create(service);
        page.ExclusionPreviewDelay = TimeSpan.Zero;
        await page.Initialization;

        // Act
        page.NewExclusion = @"**\bin\**";
        await page.PendingExclusionPreview;
        var many = page.ExclusionPreview;
        page.NewExclusion = "*.tmp";
        await page.PendingExclusionPreview;
        var none = page.ExclusionPreview;
        page.NewExclusion = "**/node*";
        await page.PendingExclusionPreview;
        var partial = page.ExclusionPreview;
        page.NewExclusion = "bad|pattern";
        await page.PendingExclusionPreview;

        // Assert
        many.ShouldBe("Уберёт из индекса 1\u202F234 записи", "разряды — узким неразрывным пробелом");
        none.ShouldBe("Ни одна запись в индексе не подходит");
        partial.ShouldBe("Уберёт из индекса не меньше 7 записей: проверена только часть индекса");
        page.HasExclusionPreview.ShouldBeFalse("неверный шаблон не считается");
        service.PreviewCount.ShouldBe(3);
    }

    [Fact]
    public async Task HardwareAcceleration_SavedAndAsksForRestart()
    {
        // Arrange
        var service = new SampleSettingsService();
        var page = Create(service);
        await page.Initialization;
        var before = RenderingInfo.HardwareAccelerated;
        RenderingInfo.HardwareAccelerated = false;
        try
        {
            // Act
            var restartBefore = page.HardwareAccelerationRestartNeeded;
            page.HardwareAcceleration = true;
            await page.PendingSave;

            // Assert
            restartBefore.ShouldBeFalse();
            page.HardwareAccelerationRestartNeeded.ShouldBeTrue("рендеринг выбирается при запуске");
            service.LastPatch!.ToJsonString().ShouldBe("""{"appearance":{"hardwareAcceleration":true}}""");
        }
        finally
        {
            RenderingInfo.HardwareAccelerated = before;
        }
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
    public async Task MainInbox_ListsInboxesFirstByDefaultAndSavesChoice()
    {
        // Arrange: два накопителя, основной не выбран
        var service = new SampleSettingsService();
        await service.PatchAsync(new System.Text.Json.Nodes.JsonObject
        {
            ["sorting"] = new System.Text.Json.Nodes.JsonObject
            {
                ["inboxes"] = new System.Text.Json.Nodes.JsonArray(
                    new System.Text.Json.Nodes.JsonObject { ["path"] = @"D:\Загрузки" },
                    new System.Text.Json.Nodes.JsonObject { ["path"] = @"D:\Skype Received", ["label"] = "Skype" }),
            },
        }, TestContext.Current.CancellationToken);
        var page = Create(service);
        await page.Initialization;

        // Act
        var initial = page.SelectedMainInbox?.Value;
        page.SelectedMainInbox = page.MainInboxOptions[1];
        await page.PendingSave;

        // Assert
        page.HasInboxes.ShouldBeTrue();
        page.MainInboxOptions.Select(static option => option.Text).ShouldBe(["Загрузки", "Skype"]);
        initial.ShouldBe(@"D:\Загрузки", "по умолчанию основная — первая в списке");
        (await service.GetAsync(TestContext.Current.CancellationToken)).Sorting.MainInbox.ShouldBe(@"D:\Skype Received");
    }

    [Fact]
    public async Task InboxQueue_SettleSecondsAndIgnorePatterns_AreSaved()
    {
        // Arrange
        var service = new SampleSettingsService();
        var page = Create(service);
        await page.Initialization;
        var shownPatterns = page.IgnorePatternsText;

        // Act: шаблоны через запятую, точку с запятой и с новой строки — пустые и повторы отбрасываются
        page.SettleSeconds = 30;
        await page.PendingSave;
        page.IgnorePatternsText = "*.crdownload, *.log;\n*.LOG, , desktop.ini";
        await page.PendingSave;

        // Assert
        shownPatterns.ShouldStartWith("*.crdownload, *.part");
        var sorting = (await service.GetAsync(TestContext.Current.CancellationToken)).Sorting;
        sorting.SettleSeconds.ShouldBe(30);
        sorting.IgnorePatterns.ShouldBe(["*.crdownload", "*.log", "desktop.ini"]);
    }

    [Fact]
    public async Task MainInbox_NoInboxes_OffersSortingPage()
    {
        // Arrange
        var commands = Substitute.For<IAppCommands>();
        var page = new SettingsViewModel(_themes, new SampleSearchService(), new HotkeyStatus(), settings: new SampleSettingsService(), dialogs: _dialogs, notices: _notices, roots: _roots, commands: commands);
        await page.Initialization;

        // Act
        page.OpenSortingCommand.Execute(null);

        // Assert
        page.HasInboxes.ShouldBeFalse();
        page.SelectedMainInbox.ShouldBeNull();
        commands.Received(1).NavigateTo(PageKeys.Inbox);
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
        page.Categories.Select(static category => category.Key).ShouldBe(["appearance", "hotkeys", "quick", "search", "index", "behavior", "integrations", "inbox", "data", "logs", "about"]);
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

using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

using Symbol = FluentIcons.Common.Symbol;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Мастер первого запуска (PLAN.md, этап 9.3): папки на выбор, добавление отмеченных в индекс, шаги, автозапуск, язык,
/// ход индексации и запоминание, что мастер пройден.
/// </summary>
public sealed class OnboardingViewModelTests : IDisposable
{
    private readonly IRootsService _roots = Substitute.For<IRootsService>();
    private readonly ISearchService _search = Substitute.For<ISearchService>();
    private readonly IThemeService _themes = Substitute.For<IThemeService>();
    private readonly IAutoStartService _autoStart = Substitute.For<IAutoStartService>();
    private readonly IAppCommands _commands = Substitute.For<IAppCommands>();
    private readonly SampleSettingsService _settings = new();
    private readonly string _folder = Directory.CreateTempSubdirectory("diskapp-onboarding-").FullName;
    private readonly string _documents;
    private readonly string _pictures;

    public OnboardingViewModelTests()
    {
        _documents = Directory.CreateDirectory(Path.Combine(_folder, "Documents")).FullName;
        _pictures = Directory.CreateDirectory(Path.Combine(_folder, "Pictures")).FullName;
        _roots.GetRootsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<RootInfo>>([]));
        _search.GetIndexStatusAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new IndexStatus { TotalEntries = 1200, Roots = [], IsIndexing = true }));
        _autoStart.IsSupported.Returns(true);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task Load_ListsRootsAndExistingStandardFolders()
    {
        // Arrange
        _roots.GetRootsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<RootInfo>>(
            [new RootInfo { Id = 1, Label = "Проекты", Path = @"D:\Projects" }]));
        using var wizard = Create();

        // Act
        await wizard.LoadAsync();

        // Assert
        wizard.Folders.Select(static folder => folder.Path).ShouldBe([@"D:\Projects", _documents, _pictures]);
        wizard.Folders[0].IsChecked.ShouldBeTrue();
        wizard.Folders[0].CanToggle.ShouldBeFalse("корень уже в индексе");
        wizard.Folders[1].IsChecked.ShouldBeTrue("документы отмечены сразу");
        wizard.Folders[1].Title.ShouldBe("Документы");
        wizard.Folders[2].IsChecked.ShouldBeFalse("изображения — по желанию");
    }

    [Fact]
    public async Task Next_FromFolders_AddsOnlyCheckedNewFolders()
    {
        // Arrange
        _roots.GetRootsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<RootInfo>>(
            [new RootInfo { Id = 1, Label = "Проекты", Path = @"D:\Projects" }]));
        using var wizard = Create();
        await wizard.LoadAsync();

        // Act
        await wizard.NextCommand.ExecuteAsync(null);

        // Assert
        await _roots.Received(1).AddAsync(_documents, Arg.Any<CancellationToken>());
        await _roots.DidNotReceive().AddAsync(_pictures, Arg.Any<CancellationToken>());
        await _roots.DidNotReceive().AddAsync(@"D:\Projects", Arg.Any<CancellationToken>());
        wizard.Step.ShouldBe(OnboardingStep.Hotkey);
        wizard.HasNote.ShouldBeFalse();
    }

    [Fact]
    public async Task Next_FolderNotAdded_ShowsNoteAndMovesOn()
    {
        // Arrange
        _roots.AddAsync(_documents, Arg.Any<CancellationToken>()).Returns(Task.FromException<RootInfo>(new InvalidOperationException("нет доступа")));
        using var wizard = Create();
        await wizard.LoadAsync();

        // Act
        await wizard.NextCommand.ExecuteAsync(null);

        // Assert
        wizard.Step.ShouldBe(OnboardingStep.Hotkey);
        wizard.Note.ShouldContain("Документы: нет доступа");
    }

    [Fact]
    public async Task Steps_WalkForwardAndBack()
    {
        // Arrange
        using var wizard = Create();
        await wizard.LoadAsync();

        // Act & Assert
        wizard.StepText.ShouldBe("Шаг 1 из 5");
        wizard.CanGoBack.ShouldBeFalse();
        wizard.NextText.ShouldBe("Далее");

        await wizard.NextCommand.ExecuteAsync(null);
        wizard.IsHotkey.ShouldBeTrue();
        wizard.StepSubtitle.ShouldStartWith(HotkeyStatus.DefaultQuickSearchText);
        wizard.CanGoBack.ShouldBeTrue();

        wizard.BackCommand.Execute(null);
        wizard.IsFolders.ShouldBeTrue();

        wizard.Step = OnboardingStep.Appearance;
        await wizard.NextCommand.ExecuteAsync(null);
        wizard.IsDone.ShouldBeTrue();
        wizard.CanGoBack.ShouldBeFalse("индексация уже идёт");
        wizard.NextText.ShouldBe("Готово");
        wizard.StepText.ShouldBe("Шаг 5 из 5");
    }

    [Fact]
    public async Task Done_ShowsIndexProgress()
    {
        // Arrange
        using var wizard = Create();
        await wizard.LoadAsync();
        wizard.Step = OnboardingStep.Appearance;

        // Act
        await wizard.NextCommand.ExecuteAsync(null);

        // Assert
        wizard.IsIndexing.ShouldBeTrue();
        wizard.ProgressText.ShouldStartWith("Индексация: найдено");
        wizard.ProgressText.ShouldContain("файлов");
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, null)]
    [InlineData(false, true, false)]
    public async Task Startup_AppliesChoiceOnlyWhenItChanges(bool choice, bool enabledNow, bool? expected)
    {
        // Arrange
        _autoStart.IsEnabled.Returns(enabledNow);
        using var wizard = Create();
        await wizard.LoadAsync();
        wizard.Step = OnboardingStep.Startup;
        wizard.StartWithWindows = choice;

        // Act
        await wizard.NextCommand.ExecuteAsync(null);

        // Assert
        wizard.Step.ShouldBe(OnboardingStep.Appearance);
        if (expected is { } value)
        {
            _autoStart.Received(1).SetEnabled(value);
        }
        else
        {
            _autoStart.DidNotReceiveWithAnyArgs().SetEnabled(default);
        }
    }

    [Fact]
    public async Task Finish_SavesCompletionAndRaisesCompleted()
    {
        // Arrange
        using var wizard = Create();
        await wizard.LoadAsync();
        var completed = 0;
        wizard.Completed += (_, _) => completed++;
        wizard.SelectedTheme = wizard.Themes.Single(static option => option.Theme == AppTheme.Dark);
        wizard.Step = OnboardingStep.Done;

        // Act
        await wizard.NextCommand.ExecuteAsync(null);

        // Assert
        completed.ShouldBe(1);
        _themes.Received(1).Apply(AppTheme.Dark);
        var saved = await _settings.GetAsync(TestContext.Current.CancellationToken);
        saved.Onboarding.Completed.ShouldBeTrue();
        saved.Onboarding.LastSeenVersion.ShouldBe(AppVersion.Current);
        _settings.LastPatch!["appearance"]!["theme"]!.GetValue<string>().ShouldBe("Dark");
    }

    [Fact]
    public async Task Skip_SavesCompletionWithoutAddingFolders()
    {
        // Arrange
        using var wizard = Create();
        await wizard.LoadAsync();
        var completed = false;
        wizard.Completed += (_, _) => completed = true;

        // Act
        await wizard.SkipCommand.ExecuteAsync(null);

        // Assert
        completed.ShouldBeTrue();
        (await _settings.GetAsync(TestContext.Current.CancellationToken)).Onboarding.Completed.ShouldBeTrue();
        await _roots.DidNotReceiveWithAnyArgs().AddAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Finish_SettingsRejected_StillCloses()
    {
        // Arrange
        using var wizard = Create();
        await wizard.LoadAsync();
        _settings.RejectWith = "сервер недоступен";
        var completed = false;
        wizard.Completed += (_, _) => completed = true;

        // Act
        await wizard.SkipCommand.ExecuteAsync(null);

        // Assert
        completed.ShouldBeTrue("в худшем случае мастер покажется при следующем запуске");
        wizard.Note.ShouldBe("сервер недоступен");
    }

    [Fact]
    public async Task Language_Selected_SavedRightAway()
    {
        // Arrange
        using var wizard = Create();
        await wizard.LoadAsync();

        // Act
        wizard.SelectedLanguage = wizard.Languages.Single(static option => option.Value == "en");

        // Assert
        _settings.LastPatch!.ToJsonString().ShouldBe("""{"appearance":{"language":"en"}}""");
        _settings.PatchCount.ShouldBe(1, "создание мастера ничего не сохраняет");
    }

    [Fact]
    public async Task AddFolder_PickedFolder_AddedChecked()
    {
        // Arrange
        var dialogs = Substitute.For<IDialogService>();
        var extra = Directory.CreateDirectory(Path.Combine(_folder, "Work")).FullName;
        dialogs.PickFolderAsync(Arg.Any<string>()).Returns(Task.FromResult<string?>(extra));
        using var wizard = Create(dialogs);
        await wizard.LoadAsync();

        // Act
        await wizard.AddFolderCommand.ExecuteAsync(null);
        await wizard.AddFolderCommand.ExecuteAsync(null);

        // Assert
        wizard.Folders.Count(folder => folder.Path == extra).ShouldBe(1, "папка не повторяется");
        wizard.Folders.Single(folder => folder.Path == extra).IsChecked.ShouldBeTrue();
        wizard.Folders.Single(folder => folder.Path == extra).Title.ShouldBe("Work");
    }

    [Fact]
    public async Task TryQuickSearch_OpensSearchWindow()
    {
        // Arrange
        using var wizard = Create();
        await wizard.LoadAsync();

        // Act
        wizard.TryQuickSearchCommand.Execute(null);

        // Assert
        _commands.Received(1).ShowQuickSearch();
    }

    private OnboardingViewModel Create(IDialogService? dialogs = null) => new(
        _roots,
        _settings,
        _search,
        _themes,
        new HotkeyStatus(),
        _autoStart,
        dialogs,
        _commands,
        [
            (_documents, "Onboarding.Folder.Documents", Symbol.Document),
            (_pictures, "Onboarding.Folder.Pictures", Symbol.Image),
            (Path.Combine(_folder, "Videos"), "Onboarding.Folder.Videos", Symbol.Video),
        ]);
}

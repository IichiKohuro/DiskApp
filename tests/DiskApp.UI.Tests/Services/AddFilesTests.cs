using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;

using FluentAvalonia.UI.Controls;


namespace DiskApp.UI.Tests.Services;

/// <summary>
/// Сценарий «Добавить» (PLAN.md, этап 8.5): варианты места, перемещение только внутри корней, постановка операций
/// и загрузка извне, буфер обмена в файл по шаблону имени, аргументы <c>--import</c> и <c>--path</c>,
/// переключатели интеграций в настройках.
/// </summary>
public sealed class AddFilesTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 5, 7, 31, TimeSpan.FromHours(3));

    private readonly string _outside = Path.Combine(Path.GetTempPath(), "DiskApp.AddTests", Guid.NewGuid().ToString("N"));
    private readonly SampleAddPreferences _preferences = new() { InboxPath = @"D:\Projects\Входящие" };
    private readonly SampleOperationsService _queue = new();
    private readonly SampleFileActions _actions = new();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly IInfoBarService _notices = Substitute.For<IInfoBarService>();

    public AddFilesTests() => Directory.CreateDirectory(_outside);

    [Fact]
    public async Task Dialog_Defaults_InboxRecentAndPinnedFolders()
    {
        // Arrange
        await _preferences.RememberTargetAsync(@"D:\Projects\Portfolio", TestContext.Current.CancellationToken);
        var dialog = new AddFilesViewModel([@"D:\Projects\DiskApp\PLAN.md"], new SampleSearchService(static () => Now), _preferences, SampleData.Roots);

        // Act
        await dialog.LoadAsync();

        // Assert
        dialog.Targets.Select(static target => target.Hint).Take(2).ShouldBe(["основная папка-накопитель", "недавнее"]);
        dialog.Targets.ShouldContain(target => target.Hint == "закреплено" && target.Label == "DiskApp");
        dialog.SelectedTarget!.Label.ShouldBe("Входящие");
        dialog.CanAdd.ShouldBeTrue();
        dialog.CanMove.ShouldBeTrue("файл уже в корне");
        dialog.SourcesText.ShouldBe("«PLAN.md»");
    }

    [Fact]
    public async Task Dialog_Query_SearchesFoldersOnly()
    {
        // Arrange
        var search = Substitute.For<ISearchService>();
        search.SearchAsync("is:dir счета", Arg.Any<CancellationToken>()).Returns(new SearchResult(
        [
            new FileEntry { FullPath = @"D:\Docs\Счета", Kind = FileKind.Folder, Root = "Документы" },
            new FileEntry { FullPath = @"D:\Docs\счета.pdf", Kind = FileKind.Pdf, Root = "Документы" },
        ], 2, TimeSpan.Zero));
        search.GetSuggestionsAsync(Arg.Any<CancellationToken>()).Returns([]);
        var dialog = new AddFilesViewModel([Path.Combine(_outside, "a.txt")], search, new SampleAddPreferences(), [@"D:\Docs"]);

        // Act
        dialog.Query = "счета";
        await dialog.PendingLoad;

        // Assert
        dialog.Targets.ShouldHaveSingleItem().Path.ShouldBe(@"D:\Docs\Счета");
        dialog.CanMove.ShouldBeFalse("файл вне корней можно только скопировать");
    }

    [Fact]
    public async Task Add_MissingFiles_WarnsWithoutDialog()
    {
        // Arrange
        var service = CreateService();
        _dialogs.ShowAddFilesAsync(Arg.Any<AddFilesViewModel>()).Returns(call =>
        {
            var dialog = call.Arg<AddFilesViewModel>();
            dialog.SelectedTarget = new AddTarget("Portfolio", @"D:\Projects\Portfolio", "закреплено", FluentIcons.Common.Symbol.Pin);
            dialog.Move = true;
            return true;
        });
        var file = @"D:\Projects\DiskApp\PLAN.md";

        // Act: путь макета, файла на диске нет.
        await service.AddAsync([file]);

        // Assert
        _notices.Received(1).Show("Нечего добавлять", Arg.Any<string>(), FAInfoBarSeverity.Warning);
        await _dialogs.DidNotReceiveWithAnyArgs().ShowAddFilesAsync(default!);
    }

    [Fact]
    public async Task Add_FileFromOutside_CopiesByUpload()
    {
        // Arrange
        var service = CreateService();
        var file = Path.Combine(_outside, "скан.pdf");
        await File.WriteAllTextAsync(file, "pdf", TestContext.Current.CancellationToken);
        _dialogs.ShowAddFilesAsync(Arg.Any<AddFilesViewModel>()).Returns(true);

        // Act
        await service.AddAsync([file]);

        // Assert
        _actions.Calls.ShouldHaveSingleItem().ShouldBe($@"CopyInto: D:\Projects\Входящие ← {file}");
        _preferences.RecentTargets.ShouldBe([@"D:\Projects\Входящие"]);
        _notices.Received(1).Show("Добавлено", Arg.Is<string>(text => text.Contains("Входящие")), FAInfoBarSeverity.Success);
    }

    [Fact]
    public async Task Add_DialogCancelled_DoesNothing()
    {
        // Arrange
        var service = CreateService();
        var file = Path.Combine(_outside, "a.txt");
        await File.WriteAllTextAsync(file, "a", TestContext.Current.CancellationToken);
        _dialogs.ShowAddFilesAsync(Arg.Any<AddFilesViewModel>()).Returns(false);

        // Act
        await service.AddAsync([file]);

        // Assert
        _actions.Calls.ShouldBeEmpty();
        _preferences.RecentTargets.ShouldBeEmpty();
    }

    [Fact]
    public async Task AddText_Markdown_SavesTemporaryMdAndCleansUp()
    {
        // Arrange
        var service = CreateService();
        string? seen = null;
        _dialogs.ShowAddFilesAsync(Arg.Any<AddFilesViewModel>()).Returns(call =>
        {
            seen = call.Arg<AddFilesViewModel>().Sources.Single();
            return true;
        });

        // Act
        await service.AddTextAsync("# План на неделю\n- купить молоко");

        // Assert
        seen.ShouldNotBeNull();
        Path.GetFileName(seen).ShouldBe("2026-10-11_05-07-31_План на неделю.md");
        File.Exists(seen).ShouldBeFalse("временная копия удаляется после добавления");
        _actions.Calls.ShouldHaveSingleItem().ShouldStartWith(@"CopyInto: D:\Projects\Входящие ← ");
    }

    [Theory]
    [InlineData("# Заголовок", true)]
    [InlineData("- пункт\n- второй", true)]
    [InlineData("см. [сайт](https://example.com)", true)]
    [InlineData("Просто текст письма", false)]
    public void LooksLikeMarkdown_DetectsCommonMarkers(string text, bool expected)
    {
        // Act & Assert
        AddFilesService.LooksLikeMarkdown(text).ShouldBe(expected);
    }

    [Fact]
    public void FileName_Template_ReplacesTokensAndInvalidChars()
    {
        // Act & Assert
        AddFilesService.FileName("{date}_{time}_{title}", "Что: где?", ".txt", Now).ShouldBe("2026-10-11_05-07-31_Что_ где.txt");
        AddFilesService.FileName("{title}", "...", ".png", Now).ShouldBe("буфер.png");
    }

    [Fact]
    public void StartupArguments_ImportAndPath()
    {
        // Act
        var import = StartupArguments.Parse(["--import", @"C:\a.txt", @"C:\b.txt"]);
        var path = StartupArguments.Parse(["--page", "browse", "--path", @"D:\Projects\DiskApp"]);

        // Assert
        import.Import.ShouldBe([@"C:\a.txt", @"C:\b.txt"]);
        path.Page.ShouldBe("browse");
        path.BrowsePath.ShouldBe(@"D:\Projects\DiskApp");
    }

    [Fact]
    public void Settings_IntegrationToggle_AppliesAndRestoresOnError()
    {
        // Arrange
        var integration = Substitute.For<IShellIntegrationService>();
        integration.IsSupported.Returns(true);
        integration.When(service => service.SetEnabled(ShellIntegrationFeature.Protocol, true)).Do(_ => throw new InvalidOperationException("Windows не дала"));
        var settings = new SettingsViewModel(Substitute.For<IThemeService>(), new SampleSearchService(), new HotkeyStatus(), integration: integration);

        // Act
        settings.SendToEnabled = true;
        settings.ProtocolEnabled = true;

        // Assert
        integration.Received(1).SetEnabled(ShellIntegrationFeature.SendTo, true);
        settings.IntegrationNote.ShouldBe("Windows не дала");
        settings.ProtocolEnabled.ShouldBeFalse("переключатель вернулся к состоянию системы");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_outside, recursive: true);
        }
        catch (IOException)
        {
            // Временная папка теста.
        }
    }

    private AddFilesService CreateService()
        => new(new SampleSearchService(static () => Now), new SampleRootsService(), _preferences, _actions,
            new OperationsViewModel(_queue, new ImmediateDispatcher(), _dialogs, _notices), _dialogs, _notices, static () => Now);
}

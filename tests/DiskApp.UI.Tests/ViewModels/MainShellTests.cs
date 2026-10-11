using Avalonia.Controls;
using Avalonia.Threading;

using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.ViewModels;
using DiskApp.UI.Views;

using FluentAvalonia.UI.Controls;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Оболочка главного окна (PLAN.md, этап 8.1): состояние индекса по событиям сервера, полоса предупреждения,
/// поиск из заголовка, панель сведений о выбранном файле.
/// </summary>
public sealed class MainShellTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 15, 0, 0, TimeSpan.FromHours(3));

    [Fact]
    public async Task ApplyIndexHealth_Offline_ShowsWarningBarAndYellowDot()
    {
        // Arrange
        var (main, _) = Create();
        await main.Initialization;

        // Act
        main.ApplyIndexHealth(IndexHealth.Offline, "Недоступно: Фото", "Недоступно: Фото");

        // Assert
        main.IsHealthWarning.ShouldBeTrue();
        main.IsHealthIndexing.ShouldBeFalse();
        main.IsIndexBarOpen.ShouldBeTrue();
        main.IndexBarTitle.ShouldBe("Часть корней недоступна");
        main.IndexBarSeverity.ShouldBe(FAInfoBarSeverity.Warning);
        main.IndexStatusShort.ShouldBe("Недоступно: Фото");
    }

    [Fact]
    public async Task ApplyIndexHealth_BackToIdle_ClosesBarAndKeepsServerText()
    {
        // Arrange
        var (main, _) = Create();
        await main.Initialization;
        main.ApplyIndexHealth(IndexHealth.Error, "Ошибка индексации: Фото", "Ошибка индексации: Фото");
        var errorShown = main.IsHealthError && main.IndexBarSeverity == FAInfoBarSeverity.Error;

        // Act
        main.ApplyIndexHealth(IndexHealth.Indexing, "Индексация", "Индексация");
        main.ApplyIndexHealth(IndexHealth.Idle, "Индекс актуален", "Индекс актуален");
        await main.RefreshAsync();

        // Assert
        errorShown.ShouldBeTrue();
        main.IsIndexBarOpen.ShouldBeFalse();
        main.IsIndexing.ShouldBeFalse();
        main.IndexStatusShort.ShouldBe("Индекс актуален", "разовый запрос не перебивает состояние от сервера");
    }

    [Fact]
    public async Task SearchFromHeader_Query_OpensSearchPageWithText()
    {
        // Arrange
        var (main, search) = Create();
        await main.Initialization;
        main.HeaderQuery = "  бюджет ";

        // Act
        main.SearchFromHeaderCommand.Execute(null);
        await search.PendingRefresh;

        // Assert
        main.SelectedPage.ShouldBeSameAs(search);
        search.Query.ShouldBe("бюджет");
        main.HeaderQuery.ShouldBeEmpty();
        search.Results[0].Entry.Name.ShouldBe("Бюджет 2026.xlsx");
    }

    [Fact]
    public void FileDetails_File_FormatsKindSizeAndDate()
    {
        // Arrange
        var entry = new FileEntry
        {
            FullPath = @"D:\Docs\Бюджет 2026.xlsx",
            Kind = FileKind.Spreadsheet,
            Root = "Документы",
            Size = 348_160,
            Modified = new DateTimeOffset(2026, 10, 9, 14, 5, 0, TimeSpan.FromHours(3)),
            IsCloudOnly = true,
            Tags = ["работа"],
        };

        // Act
        var details = new FileDetails(entry);

        // Assert
        details.KindText.ShouldBe("Таблица · .xlsx");
        details.SizeText.ShouldStartWith("340 КБ");
        details.SizeText.ShouldContain("байт");
        details.ModifiedText.ShouldContain("2026");
        details.HasAvailability.ShouldBeTrue();
        details.HasTags.ShouldBeTrue();
        new FileDetails(entry with { Kind = FileKind.Folder, FullPath = @"D:\Docs" }).SizeText.ShouldBe("—");
    }

    [Fact]
    public async Task SelectedDetails_FollowsSelection()
    {
        // Arrange
        var (_, search) = Create();
        search.Query = "отчёт";
        await search.PendingRefresh;

        // Act
        var first = search.SelectedDetails;
        search.MoveSelection(1);

        // Assert
        first.ShouldNotBeNull().Entry.Name.ShouldBe("Отчёт за 3 квартал 2026.docx");
        search.SelectedDetails.ShouldNotBeNull().Entry.Name.ShouldBe("Отчёт за 2 квартал 2026.docx");
        search.SelectedDetails.ShouldBeSameAs(search.SelectedDetails, "сведения не пересоздаются при каждом чтении");
    }

    [Fact]
    public Task SearchPage_WideWindow_ShowsDetailsPaneForSelection() => HeadlessTest.RunAsync(static async () =>
    {
        // Arrange
        var (_, search) = Create();
        search.Query = "бюджет";
        await search.PendingRefresh;
        var page = new SearchPageView { DataContext = search };
        var window = new Window { Width = 1200, Height = 800, Content = page };

        // Act
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Assert
        var card = page.FindControl<Border>("DetailsCard")!;
        card.IsVisible.ShouldBeTrue();
        card.Child.ShouldBeOfType<DetailsPane>();
        window.Width = 800;
        Dispatcher.UIThread.RunJobs();
        card.IsVisible.ShouldBeFalse("на узком окне остаётся только список");
        window.Close();
    });

    private static (MainViewModel Main, QuickSearchViewModel Search) Create()
    {
        var service = new SampleSearchService(static () => Now);
        var commands = NSubstitute.Substitute.For<IAppCommands>();
        var themes = NSubstitute.Substitute.For<IThemeService>();
        var hotkeys = new HotkeyStatus();
        var search = new QuickSearchViewModel(service, static () => Now) { DebounceDelay = TimeSpan.Zero };
        var main = new MainViewModel(
            new HomeViewModel(service, commands, hotkeys, static () => Now),
            search,
            new SettingsViewModel(themes, service, hotkeys),
            service,
            static () => Now);
        return (main, search);
    }
}

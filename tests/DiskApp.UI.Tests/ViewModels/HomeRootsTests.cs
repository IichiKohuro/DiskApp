using DiskApp.Client;
using DiskApp.Contracts.Roots;
using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.Tests.Services;
using DiskApp.UI.ViewModels;

using FluentAvalonia.UI.Controls;


using RootStatus = DiskApp.Contracts.Roots.RootStatus;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Главная (PLAN.md, этап 8.2): карточки корней со сводкой и действиями, добавление папок, закреплённые и изменённые
/// за сутки файлы; перевод корней API в модель интерфейса.
/// </summary>
public sealed class HomeRootsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 15, 0, 0, TimeSpan.FromHours(3));

    [Fact]
    public async Task Refresh_RootsAndLists_FillsCardsWithSummaries()
    {
        // Arrange & Act
        var (home, _, _) = Create();
        await home.Initialization;

        // Assert
        home.Roots.Count.ShouldBe(SampleData.Roots.Count);
        home.HasNoRoots.ShouldBeFalse();
        home.Roots[0].SummaryText.Replace((char)0x202F, ' ').Replace((char)0xA0, ' ').ShouldBe("10 000 файлов · 800 папок · " + DiskApp.UI.Text.Formatting.Size(1_500_000_000L).Replace((char)0x202F, ' ').Replace((char)0xA0, ' '));
        home.Roots[0].StateText.ShouldBe("Актуален");
        home.Roots[3].IsProblem.ShouldBeTrue();
        home.Roots[3].StateText.ShouldBe("Недоступен: Диск E: не подключён");
        home.Roots[3].CanPause.ShouldBeFalse();
        home.PinnedFiles.ShouldNotBeEmpty();
        home.PinnedFiles.ShouldAllBe(hit => hit.Entry.IsFavorite);
        home.ChangedToday.ShouldNotBeEmpty();
        home.ChangedToday.ShouldAllBe(hit => hit.Entry.Modified >= Now.AddDays(-1) && hit.Entry.Kind != FileKind.Folder);
        home.ChangedToday.Count.ShouldBeLessThanOrEqualTo(6);
    }

    [Fact]
    public async Task RootCard_PauseAndRescan_CallServiceAndUpdateState()
    {
        // Arrange
        var (home, roots, _) = Create();
        await home.Initialization;
        var card = home.Roots[0];

        // Act
        await card.TogglePauseCommand.ExecuteAsync(null);
        var paused = (card.IsPaused, card.PauseText, card.StateText);
        await card.TogglePauseCommand.ExecuteAsync(null);
        await card.RescanCommand.ExecuteAsync(null);

        // Assert
        paused.ShouldBe((true, "Продолжить", "На паузе"));
        card.IsScanning.ShouldBeTrue();
        card.StateText.ShouldBe("Сканирование");
        roots.Calls.ShouldBe(["pause:1", "resume:1", "rescan:1"]);
    }

    [Fact]
    public async Task AddRoots_NewFolders_AddsAndNotifies()
    {
        // Arrange
        var (home, roots, notices) = Create();
        await home.Initialization;

        // Act
        var added = await home.AddRootsAsync([@"F:\Архив", @"G:\Видео"]);

        // Assert
        added.ShouldBe(2);
        roots.Added.ShouldBe([@"F:\Архив", @"G:\Видео"]);
        home.Roots.Count.ShouldBe(SampleData.Roots.Count + 2);
        notices.Received(1).Show("Папки добавлены в индекс", Arg.Is<string>(text => text.Contains("Архив")), FAInfoBarSeverity.Success);
    }

    [Fact]
    public async Task AddRoots_ServerRejects_WarnsWithReason()
    {
        // Arrange
        var roots = Substitute.For<IRootsService>();
        roots.GetRootsAsync(Arg.Any<CancellationToken>()).Returns([]);
        roots.AddAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<Task<RootInfo>>(_ => throw new InvalidOperationException("Папка уже добавлена"));
        var notices = Substitute.For<IInfoBarService>();
        var home = new HomeViewModel(new SampleSearchService(static () => Now), Substitute.For<IAppCommands>(), new HotkeyStatus(), roots, notices, static () => Now);
        await home.Initialization;

        // Act
        var added = await home.AddRootsAsync([@"D:\Projects"]);

        // Assert
        added.ShouldBe(0);
        home.HasNoRoots.ShouldBeTrue();
        notices.Received(1).Show("Не все папки добавлены", Arg.Is<string>(text => text.Contains("Папка уже добавлена")), FAInfoBarSeverity.Warning);
    }

    [Theory]
    [InlineData(RootStatus.Online, true, RootState.Online)]
    [InlineData(RootStatus.Offline, true, RootState.Offline)]
    [InlineData(RootStatus.Scanning, true, RootState.Scanning)]
    [InlineData(RootStatus.Paused, true, RootState.Paused)]
    [InlineData(RootStatus.Error, true, RootState.Error)]
    [InlineData(RootStatus.Online, false, RootState.Disabled)]
    public void ApiRoots_ToInfo_MapsState(RootStatus status, bool enabled, RootState expected)
    {
        // Arrange
        var dto = new RootDto(1, @"D:\Docs", "Docs", enabled, false, false, null, Now, status, null);

        // Act & Assert
        ApiRootsService.ToInfo(dto, null).State.ShouldBe(expected);
    }

    [Fact]
    public async Task ApiRoots_GetRoots_AddsProgressAndSummary()
    {
        // Arrange
        var (api, fake) = FakeDiskAppApi.Create();
        fake.Roots = [new RootDto(7, @"D:\Docs", "Docs", true, false, false, null, null, RootStatus.Scanning, null)];
        fake.Status = new IndexStatusDto(IndexerState.Indexing, false,
            [new RootIndexStatusDto(7, @"D:\Docs", "Docs", RootStatus.Scanning, null, IndexTaskKind.FullScan, false, new IndexProgressDto(7, IndexPhase.Enumerating, 1500, 1400, null, 300, 5), null)]);
        fake.Handlers[nameof(IDiskAppApi.GetRootStatsAsync)] = args => Task.FromResult(new RootStatsDto((long)args[0]!, 10, 2, 2048));
        var service = new ApiRootsService(api);

        // Act
        var roots = await service.GetRootsAsync(TestContext.Current.CancellationToken);
        var summary = await service.GetSummaryAsync(7, TestContext.Current.CancellationToken);

        // Assert
        var root = roots.ShouldHaveSingleItem();
        root.State.ShouldBe(RootState.Scanning);
        root.Scanned.ShouldBe(1500);
        summary.ShouldBe(new RootSummary(10, 2, 2048));
        new RootCardViewModel(root, service, static () => Now, static (_, _) => { }).StateText.Replace((char)0x202F, ' ').Replace((char)0xA0, ' ').ShouldBe("Сканирование: просмотрено 1 500");
    }

    private static (HomeViewModel Home, SampleRootsService Roots, IInfoBarService Notices) Create()
    {
        var roots = new SampleRootsService();
        var notices = Substitute.For<IInfoBarService>();
        var home = new HomeViewModel(new SampleSearchService(static () => Now), Substitute.For<IAppCommands>(), new HotkeyStatus(), roots, notices, static () => Now);
        return (home, roots, notices);
    }
}

using DiskApp.Client;
using DiskApp.Contracts.Entries;
using DiskApp.Contracts.Roots;
using DiskApp.UI.Services;
using DiskApp.UI.Tests.Services;
using DiskApp.UI.ViewModels;

using FluentAvalonia.UI.Controls;

using ContractKind = DiskApp.Contracts.Entries.FileKind;
using FileKind = DiskApp.UI.Models.FileKind;
using RootStatus = DiskApp.Contracts.Roots.RootStatus;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Обзор папок (PLAN.md, этап 8.3): первый корень при открытии, переходы и история, крошки, фильтры и сортировка,
/// выбор и строка состояния, действия с подтверждением, ленивое дерево; обзор поверх API.
/// </summary>
public sealed class BrowseViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 15, 0, 0, TimeSpan.FromHours(3));
    private const string Projects = @"D:\Projects";

    [Fact]
    public async Task Initialization_OpensFirstRootAndBuildsTree()
    {
        // Arrange & Act
        var (browse, _, _, _) = Create();
        await browse.Initialization;

        // Assert
        browse.CurrentPath.ShouldBe(Projects);
        browse.Tree.Count.ShouldBe(SampleData.Roots.Count);
        browse.Crumbs.ShouldHaveSingleItem().Path.ShouldBe(Projects);
        browse.Items.Select(static hit => hit.Entry.Name).ShouldContain("DiskApp");
        browse.Items.First().Entry.Kind.ShouldBe(FileKind.Folder, "папки идут первыми");
        browse.CanGoUp.ShouldBeFalse("корень — верх");
        browse.StatusText.ShouldStartWith(browse.Items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Navigate_IntoFolderBackForwardUp_KeepsHistoryAndCrumbs()
    {
        // Arrange
        var (browse, _, _, _) = Create();
        await browse.Initialization;

        // Act
        await browse.OpenItemCommand.ExecuteAsync(browse.Items.Single(static hit => hit.Entry.Name == "DiskApp"));
        await browse.NavigateAsync(Projects + @"\DiskApp\src");
        var deep = (browse.CurrentPath, Crumbs: browse.Crumbs.Select(static crumb => crumb.Label).ToArray());
        await browse.GoBackCommand.ExecuteAsync(null);
        var back = browse.CurrentPath;
        await browse.GoForwardCommand.ExecuteAsync(null);
        await browse.GoUpCommand.ExecuteAsync(null);

        // Assert
        deep.CurrentPath.ShouldBe(Projects + @"\DiskApp\src");
        deep.Crumbs.ShouldBe(["Projects", "DiskApp", "src"]);
        back.ShouldBe(Projects + @"\DiskApp");
        browse.CurrentPath.ShouldBe(Projects + @"\DiskApp");
        browse.CanGoBack.ShouldBeTrue();
    }

    [Fact]
    public async Task Navigate_OutsideRoots_ShowsErrorAndKeepsFolder()
    {
        // Arrange
        var (browse, _, _, _) = Create();
        await browse.Initialization;

        // Act
        browse.AddressText = @"Z:\Чужое";
        await browse.GoToAddressCommand.ExecuteAsync(null);

        // Assert
        browse.HasError.ShouldBeTrue();
        browse.ErrorText.ShouldContain("вне добавленных корней");
        browse.CurrentPath.ShouldBe(Projects);
        browse.AddressText.ShouldBe(Projects);
    }

    [Fact]
    public async Task FilterAndKind_NarrowItemsAndStatus()
    {
        // Arrange
        var (browse, _, _, _) = Create();
        await browse.NavigateAsync(Projects + @"\DiskApp");
        var all = browse.Items.Count;

        // Act
        browse.Filter = "md";
        var filtered = browse.Items.Select(static hit => hit.Entry.Name).ToArray();
        browse.Filter = string.Empty;
        browse.SelectedKind = browse.KindOptions.Single(static option => option.Label == "Папки");

        // Assert
        filtered.ShouldBe(["PLAN.md", "README.md"], ignoreOrder: true);
        browse.Items.ShouldAllBe(hit => hit.Entry.Kind == FileKind.Folder);
        browse.StatusText.ShouldContain($" из {all}");
    }

    [Fact]
    public async Task EmptyHint_FilterLeavesNothing_ExplainsHowToSeeAll()
    {
        // Arrange
        var (browse, _, _, _) = Create();
        await browse.NavigateAsync(Projects + @"\DiskApp");

        // Act
        var before = browse.HasEmptyHint;
        browse.Filter = "нет-такого-имени";
        var hint = browse.EmptyHint;
        browse.Filter = string.Empty;

        // Assert
        before.ShouldBeFalse();
        hint.ShouldStartWith("Фильтр ничего не оставил");
        browse.HasEmptyHint.ShouldBeFalse();
    }

    [Fact]
    public async Task SortByKind_GroupsByKindThenName()
    {
        // Arrange
        var (browse, _, _, _) = Create();
        await browse.NavigateAsync(@"C:\Users\Иван\Downloads");

        // Act
        browse.SelectedSort = browse.SortOptions.Single(static option => option.Sort == BrowseSort.Kind);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // Assert
        var kinds = browse.Items.Select(static hit => hit.Entry.Kind).ToArray();
        kinds.ShouldBe(kinds.Order().ToArray());
    }

    [Fact]
    public async Task Selection_ShowsDetailsAndSizeInStatus()
    {
        // Arrange
        var (browse, _, _, _) = Create();
        await browse.NavigateAsync(Projects + @"\DiskApp");
        var files = browse.Items.Where(static hit => hit.Entry.Kind != FileKind.Folder).Take(2).ToArray();

        // Act
        browse.UpdateSelection([files[0]]);
        var single = browse.SelectedDetails;
        browse.UpdateSelection(files);

        // Assert
        single.ShouldNotBeNull().Entry.ShouldBe(files[0].Entry);
        browse.SelectedDetails.ShouldBeNull("сведения — для одного файла");
        browse.StatusText.ShouldContain("выбрано 2");
        browse.StatusText.ShouldContain("КБ");
    }

    [Fact]
    public async Task Delete_Confirmed_MovesSelectionToRecycleBin()
    {
        // Arrange
        var (browse, actions, dialogs, _) = Create();
        await browse.NavigateAsync(Projects + @"\DiskApp");
        var file = browse.Items.First(static hit => hit.Entry.Kind != FileKind.Folder);
        browse.UpdateSelection([file]);
        dialogs.ConfirmAsync(default!, default!, default!, default!).ReturnsForAnyArgs(Task.FromResult(true));

        // Act
        await browse.DeleteCommand.ExecuteAsync(null);

        // Assert
        await dialogs.ReceivedWithAnyArgs(1).ConfirmAsync(default!, default!, default!, default!);
        actions.Calls.ShouldHaveSingleItem().ShouldBe("Recycle: " + file.Entry.FullPath);
    }

    [Fact]
    public async Task Delete_Declined_DoesNothing()
    {
        // Arrange
        var (browse, actions, dialogs, _) = Create();
        await browse.NavigateAsync(Projects + @"\DiskApp");
        browse.UpdateSelection([browse.Items.First(static hit => hit.Entry.Kind != FileKind.Folder)]);
        dialogs.ConfirmAsync(default!, default!, default!, default!).ReturnsForAnyArgs(Task.FromResult(false));

        // Act
        await browse.DeleteCommand.ExecuteAsync(null);

        // Assert
        actions.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Rename_PromptSelectsStem_AndRenames()
    {
        // Arrange
        var (browse, actions, dialogs, _) = Create();
        await browse.NavigateAsync(Projects + @"\DiskApp");
        var file = browse.Items.Single(static hit => hit.Entry.Name == "PLAN.md");
        browse.UpdateSelection([file]);
        dialogs.PromptAsync(default!, default!, default!, default!, default).ReturnsForAnyArgs(Task.FromResult<string?>("ROADMAP.md"));

        // Act
        await browse.RenameCommand.ExecuteAsync(null);

        // Assert
        await dialogs.Received(1).PromptAsync("Переименовать", "Новое имя для «PLAN.md»", "PLAN.md", "Переименовать", 4);
        actions.Calls.ShouldHaveSingleItem().ShouldEndWith(" → ROADMAP.md");
    }

    [Fact]
    public async Task CopyPath_Selection_NotifiesSuccess()
    {
        // Arrange
        var (browse, actions, _, notices) = Create();
        await browse.NavigateAsync(Projects + @"\DiskApp");
        browse.UpdateSelection([.. browse.Items.Take(2)]);

        // Act
        await browse.CopyPathCommand.ExecuteAsync(null);

        // Assert
        actions.Calls.ShouldHaveSingleItem().ShouldStartWith("CopyPaths: ");
        notices.Received(1).Show("Путь скопирован", "2 пути", FAInfoBarSeverity.Success);
    }

    [Fact]
    public async Task TreeNode_Expand_LoadsSubfoldersOnce()
    {
        // Arrange
        var (browse, _, _, _) = Create();
        await browse.Initialization;
        var projects = browse.Tree[0];
        var placeholder = projects.Children.ShouldHaveSingleItem().IsPlaceholder;

        // Act
        projects.IsExpanded = true;
        await projects.LoadAsync();

        // Assert
        placeholder.ShouldBeTrue();
        projects.Children.Select(static node => node.Name).ShouldBe(["Archive", "DiskApp", "Portfolio", "ShopBot"], ignoreOrder: true);
        projects.Children.ShouldAllBe(node => !node.IsPlaceholder);
    }

    [Fact]
    public async Task ApiBrowse_ListAsync_MapsItemsWithRootLabels()
    {
        // Arrange
        var (api, fake) = FakeDiskAppApi.Create();
        fake.Roots = [new RootDto(3, Projects, "Проекты", true, false, false, null, null, RootStatus.Online, null)];
        BrowseOptions? options = null;
        fake.Handlers[nameof(IDiskAppApi.BrowseAsync)] = args =>
        {
            options = (BrowseOptions?)args[1];
            return Task.FromResult(new BrowseResponse(Projects, 3, null,
            [
                new EntryDto { Id = 1, RootId = 3, Name = "src", FullPath = Projects + @"\src", Kind = ContractKind.Folder, IsDirectory = true },
                new EntryDto { Id = 2, RootId = 3, Name = "a.txt", FullPath = Projects + @"\a.txt", Kind = ContractKind.Document },
            ], Truncated: false));
        };
        var service = new ApiBrowseService(api);

        // Act
        var listing = await service.ListAsync(Projects, BrowseSort.Size, descending: true, TestContext.Current.CancellationToken);

        // Assert
        listing.Items.Select(static entry => entry.Name).ShouldBe(["src", "a.txt"]);
        listing.Items.ShouldAllBe(entry => entry.Root == "Проекты");
        options.ShouldNotBeNull();
        options.Sort.ShouldBe(DiskApp.Contracts.Search.SearchSort.Size);
        options.Descending.ShouldBe(true);
    }

    private static (BrowseViewModel Browse, SampleFileActions Actions, IDialogService Dialogs, IInfoBarService Notices) Create()
    {
        var actions = new SampleFileActions();
        var dialogs = NSubstitute.Substitute.For<IDialogService>();
        var notices = NSubstitute.Substitute.For<IInfoBarService>();
        var browse = new BrowseViewModel(new SampleBrowseService(static () => Now), new SampleRootsService(), new SampleSearchService(static () => Now), actions, dialogs, notices, static () => Now);
        return (browse, actions, dialogs, notices);
    }
}

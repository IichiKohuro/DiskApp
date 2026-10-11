using DiskApp.Client;
using DiskApp.Contracts.Entries;
using DiskApp.Contracts.Operations;
using DiskApp.Contracts.Roots;
using DiskApp.UI.Models;
using DiskApp.UI.Services;
using DiskApp.UI.Tests.Services;
using DiskApp.UI.ViewModels;

using ContractKind = DiskApp.Contracts.Entries.FileKind;

namespace DiskApp.UI.Tests.ViewModels;

/// <summary>
/// Обзор и операции поверх фейкового <see cref="IDiskAppApi"/> (PLAN.md, этап 8.8): те же ViewModel, что в приложении,
/// с сервисами API вместо макетов — проверяется, какие запросы уходят на сервер и как страницы показывают ответы.
/// </summary>
public sealed class ApiBackedPagesTests : IAsyncDisposable
{
    private const string Root = @"D:\Projects";
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.FromHours(3));

    private readonly IDiskAppApi _api;
    private readonly FakeDiskAppApi _fake;
    private readonly List<OperationRequest> _requests = [];
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly IInfoBarService _notices = Substitute.For<IInfoBarService>();
    private readonly DiskAppHubConnection _hub = new(new FixedServerEndpointProvider(new ServerEndpoint(new Uri("http://127.0.0.1:9"))));
    private List<EntryDto> _items =
    [
        Dto(1, "src", ContractKind.Folder, isDirectory: true),
        Dto(2, "PLAN.md", ContractKind.Document),
        Dto(3, "README.md", ContractKind.Document),
    ];

    public ApiBackedPagesTests()
    {
        (_api, _fake) = FakeDiskAppApi.Create();
        _fake.Roots = [new RootDto(1, Root, "Проекты", true, false, false, null, null, RootStatus.Online, null)];
        _fake.Handlers[nameof(IDiskAppApi.BrowseAsync)] = args =>
        {
            var path = (string)args[0]!;
            var items = string.Equals(path, Root, StringComparison.OrdinalIgnoreCase) ? _items : [];
            return Task.FromResult(new BrowseResponse(path, 1, null, items, Truncated: false));
        };
        _fake.Handlers[nameof(IDiskAppApi.EnqueueOperationAsync)] = args =>
        {
            var request = (OperationRequest)args[0]!;
            _requests.Add(request);
            return Task.FromResult(Operation(request, OperationState.Completed, canUndo: request.Kind is OperationKind.Move or OperationKind.Rename));
        };
        _fake.Handlers[nameof(IDiskAppApi.GetOperationsAsync)] = _ => Task.FromResult<IReadOnlyList<OperationDto>>([]);
    }

    [Fact]
    public async Task Browse_OpensRootFromServerListing()
    {
        // Arrange & Act
        var browse = await CreateBrowseAsync();

        // Assert
        browse.CurrentPath.ShouldBe(Root);
        browse.Items.Select(static hit => hit.Entry.Name).ShouldBe(["src", "PLAN.md", "README.md"]);
        browse.Items.ShouldAllBe(hit => hit.Entry.Root == "Проекты");
        _fake.Calls.ShouldContain(nameof(IDiskAppApi.BrowseAsync));
    }

    [Fact]
    public async Task Browse_Rename_SendsRenameOperationAndReloads()
    {
        // Arrange
        var browse = await CreateBrowseAsync();
        browse.UpdateSelection([browse.Items.Single(static hit => hit.Entry.Name == "PLAN.md")]);
        _dialogs.PromptAsync("Переименовать", Arg.Any<string>(), "PLAN.md", "Переименовать", Arg.Any<int>()).Returns("ПЛАН.md");
        _items = [.. _items.Select(static dto => dto.Name == "PLAN.md" ? dto with { Name = "ПЛАН.md", FullPath = Root + @"\ПЛАН.md" } : dto)];

        // Act
        await browse.RenameCommand.ExecuteAsync(null);

        // Assert
        var request = _requests.ShouldHaveSingleItem();
        request.Kind.ShouldBe(OperationKind.Rename);
        request.Sources.ShouldBe([Root + @"\PLAN.md"]);
        request.NewName.ShouldBe("ПЛАН.md");
        request.Conflicts.ShouldBe(ConflictPolicy.Ask);
        browse.Items.ShouldContain(hit => hit.Entry.Name == "ПЛАН.md");
    }

    [Fact]
    public async Task Browse_Delete_SendsRecycleNotPermanentDelete()
    {
        // Arrange
        var browse = await CreateBrowseAsync();
        browse.UpdateSelection([.. browse.Items.Where(static hit => hit.Entry.Name.EndsWith(".md", StringComparison.Ordinal))]);
        _dialogs.ConfirmAsync("В корзину", Arg.Any<string>(), "В корзину").Returns(true);

        // Act
        await browse.DeleteCommand.ExecuteAsync(null);

        // Assert
        var request = _requests.ShouldHaveSingleItem();
        request.Kind.ShouldBe(OperationKind.Delete);
        request.Permanent.ShouldBeFalse("файлы пользователя удаляются только в корзину");
        request.Sources.ShouldBe([Root + @"\PLAN.md", Root + @"\README.md"]);
    }

    [Fact]
    public async Task Browse_CutPaste_QueuesMoveOnOperationsPage()
    {
        // Arrange
        var operations = new OperationsViewModel(new ApiOperationsService(_api, _hub), new ImmediateDispatcher(), _dialogs, _notices);
        var browse = await CreateBrowseAsync(operations);
        browse.UpdateSelection([browse.Items.Single(static hit => hit.Entry.Name == "README.md")]);
        await browse.CutCommand.ExecuteAsync(null);
        await browse.NavigateAsync(Root + @"\src");

        // Act
        await browse.PasteAsync();

        // Assert
        var request = _requests.ShouldHaveSingleItem();
        request.Kind.ShouldBe(OperationKind.Move);
        request.Sources.ShouldBe([Root + @"\README.md"]);
        request.Destination.ShouldBe(Root + @"\src");
        var item = operations.Items.ShouldHaveSingleItem();
        item.Info.Kind.ShouldBe(FileOperationKind.Move);
        item.Info.CanUndo.ShouldBeTrue();
        browse.HasClipboard.ShouldBeFalse("после переноса вырезанное не вставляется второй раз");
    }

    [Fact]
    public async Task Operations_CancelAndUndo_GoToServer()
    {
        // Arrange
        var cancelled = new List<Guid>();
        var undone = new List<Guid>();
        _fake.Handlers[nameof(IDiskAppApi.CancelOperationAsync)] = args =>
        {
            cancelled.Add((Guid)args[0]!);
            return Task.CompletedTask;
        };
        _fake.Handlers[nameof(IDiskAppApi.UndoOperationAsync)] = args =>
        {
            undone.Add((Guid)args[0]!);
            return Task.FromResult(Operation(new OperationRequest { Kind = OperationKind.Move, Sources = [Root + @"\src\README.md"], Destination = Root }, OperationState.Completed, canUndo: false));
        };
        var operations = new OperationsViewModel(new ApiOperationsService(_api, _hub), new ImmediateDispatcher(), _dialogs, _notices);
        var started = await operations.EnqueueAsync(new NewOperation(FileOperationKind.Move, [Root + @"\README.md"], Root + @"\src"));
        var item = operations.Items.Single(candidate => candidate.Info.Id == started!.Id);

        // Act
        await operations.CancelCommand.ExecuteAsync(item);
        var undid = await operations.UndoLastAsync();

        // Assert
        cancelled.ShouldBe([started!.Id]);
        undid.ShouldBeTrue();
        undone.ShouldBe([started.Id]);
        item.Info.CanUndo.ShouldBeFalse("результат отменён один раз");
        (await operations.UndoLastAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task Operations_Load_ShowsServerQueue()
    {
        // Arrange
        var running = Operation(new OperationRequest { Kind = OperationKind.Copy, Sources = [Root + @"\PLAN.md"], Destination = @"D:\Backup" }, OperationState.Running, canUndo: false);
        _fake.Handlers[nameof(IDiskAppApi.GetOperationsAsync)] = _ => Task.FromResult<IReadOnlyList<OperationDto>>([running]);

        // Act
        var operations = new OperationsViewModel(new ApiOperationsService(_api, _hub), new ImmediateDispatcher(), _dialogs, _notices);
        await WaitUntilAsync(() => operations.Items.Count > 0);

        // Assert
        var item = operations.Items.ShouldHaveSingleItem();
        item.Info.Id.ShouldBe(running.Id);
        item.IsActive.ShouldBeTrue();
        item.CanCancel.ShouldBeTrue();
    }

    public async ValueTask DisposeAsync() => await _hub.DisposeAsync();

    private static EntryDto Dto(long id, string name, ContractKind kind, bool isDirectory = false) => new()
    {
        Id = id,
        RootId = 1,
        Name = name,
        FullPath = Root + @"\" + name,
        RelPath = name,
        Kind = kind,
        IsDirectory = isDirectory,
        ModifiedUtc = Now.AddHours(-1),
    };

    private static OperationDto Operation(OperationRequest request, OperationState state, bool canUndo)
        => new(Guid.NewGuid(), request.Kind, state, request.Sources, request.Destination, Now, state == OperationState.Completed ? Now : null,
            new OperationProgressDto(Guid.Empty, 1, 1, 10, 10, null, 0, null), null, canUndo, null);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private async Task<BrowseViewModel> CreateBrowseAsync(IOperationsCoordinator? operations = null)
    {
        var browse = new BrowseViewModel(
            new ApiBrowseService(_api),
            new ApiRootsService(_api),
            new ApiSearchService(_api),
            new ApiFileActions(_api, Substitute.For<IClipboardService>()),
            _dialogs,
            _notices,
            operations,
            static () => Now);
        await browse.Initialization;
        await browse.NavigateAsync(Root);
        return browse;
    }
}

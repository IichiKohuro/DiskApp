using DiskApp.Client;
using DiskApp.Contracts.Entries;
using DiskApp.Contracts.Operations;
using DiskApp.UI.Models;
using DiskApp.UI.Services;

using FileKind = DiskApp.UI.Models.FileKind;

namespace DiskApp.UI.Tests.Services;

/// <summary>
/// Действия над файлами поверх клиента API (PLAN.md, этап 7.3): операции очереди сервера с ожиданием итога,
/// конфликт имени при переименовании, буфер обмена оболочки, избранное.
/// </summary>
public sealed class ApiFileActionsTests
{
    private static readonly FileEntry Report = new()
    {
        Id = 7,
        FullPath = @"D:\Docs\Отчёт.docx",
        Kind = FileKind.Document,
        Root = "Документы",
        NameHighlights = [new HighlightRange(0, 5)],
    };

    private readonly FakeClipboard _clipboard = new();

    [Fact]
    public async Task Rename_Completed_EnqueuesRenameAndReturnsNewPath()
    {
        // Arrange
        var (api, fake) = FakeDiskAppApi.Create();
        OperationRequest? sent = null;
        fake.Handlers[nameof(IDiskAppApi.EnqueueOperationAsync)] = args =>
        {
            sent = (OperationRequest)args[0]!;
            return Task.FromResult(Operation(OperationState.Queued));
        };
        fake.Handlers[nameof(IDiskAppApi.GetOperationAsync)] = _ => Task.FromResult(Operation(OperationState.Completed));
        var actions = new ApiFileActions(api, _clipboard);

        // Act
        var renamed = await actions.RenameAsync(Report, "  Отчёт Q3.docx ", TestContext.Current.CancellationToken);

        // Assert
        sent.ShouldNotBeNull();
        sent.Kind.ShouldBe(OperationKind.Rename);
        sent.Sources.ShouldBe([Report.FullPath]);
        sent.NewName.ShouldBe("Отчёт Q3.docx");
        sent.Conflicts.ShouldBe(ConflictPolicy.Ask);
        renamed.FullPath.ShouldBe(@"D:\Docs\Отчёт Q3.docx");
        renamed.Id.ShouldBe(Report.Id);
        renamed.NameHighlights.ShouldBeNull("старая подсветка к новому имени не относится");
    }

    [Fact]
    public async Task Rename_NameTaken_CancelsOperationAndExplains()
    {
        // Arrange
        var (api, fake) = FakeDiskAppApi.Create();
        var cancelled = false;
        fake.Handlers[nameof(IDiskAppApi.EnqueueOperationAsync)] = _ => Task.FromResult(Operation(OperationState.WaitingDecision));
        fake.Handlers[nameof(IDiskAppApi.CancelOperationAsync)] = _ =>
        {
            cancelled = true;
            return Task.CompletedTask;
        };
        var actions = new ApiFileActions(api, _clipboard);

        // Act
        var error = await Should.ThrowAsync<InvalidOperationException>(() => actions.RenameAsync(Report, "Бюджет.docx", TestContext.Current.CancellationToken));

        // Assert
        cancelled.ShouldBeTrue();
        error.Message.ShouldBe("«Бюджет.docx» уже есть в этой папке.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("a:b.txt")]
    [InlineData("CON.txt")]
    [InlineData("name.")]
    public async Task Rename_InvalidName_ThrowsWithoutCallingServer(string name)
    {
        // Arrange
        var (api, fake) = FakeDiskAppApi.Create();
        var actions = new ApiFileActions(api, _clipboard);

        // Act & Assert
        await Should.ThrowAsync<ArgumentException>(() => actions.RenameAsync(Report, name, TestContext.Current.CancellationToken));
        fake.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Rename_SameName_ReturnsEntryWithoutCallingServer()
    {
        // Arrange
        var (api, fake) = FakeDiskAppApi.Create();
        var actions = new ApiFileActions(api, _clipboard);

        // Act
        var result = await actions.RenameAsync(Report, Report.Name, TestContext.Current.CancellationToken);

        // Assert
        result.ShouldBeSameAs(Report);
        fake.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task MoveToRecycleBin_Failed_ThrowsServerMessage()
    {
        // Arrange
        var (api, fake) = FakeDiskAppApi.Create();
        OperationRequest? sent = null;
        fake.Handlers[nameof(IDiskAppApi.EnqueueOperationAsync)] = args =>
        {
            sent = (OperationRequest)args[0]!;
            return Task.FromResult(Operation(OperationState.Running));
        };
        fake.Handlers[nameof(IDiskAppApi.GetOperationAsync)] = _ => Task.FromResult(Operation(OperationState.Failed) with { Message = "Для «X» нет корзины." });
        var actions = new ApiFileActions(api, _clipboard);

        // Act
        var error = await Should.ThrowAsync<InvalidOperationException>(() => actions.MoveToRecycleBinAsync([Report], TestContext.Current.CancellationToken));

        // Assert
        sent!.Kind.ShouldBe(OperationKind.Delete);
        sent.Permanent.ShouldBeFalse("из интерфейса — только в корзину");
        error.Message.ShouldBe("Для «X» нет корзины.");
    }

    [Fact]
    public async Task MoveToRecycleBin_StillRunning_TimesOut()
    {
        // Arrange
        var (api, fake) = FakeDiskAppApi.Create();
        fake.Handlers[nameof(IDiskAppApi.EnqueueOperationAsync)] = _ => Task.FromResult(Operation(OperationState.Running));
        fake.Handlers[nameof(IDiskAppApi.GetOperationAsync)] = _ => Task.FromResult(Operation(OperationState.Running));
        var actions = new ApiFileActions(api, _clipboard) { OperationTimeout = TimeSpan.FromMilliseconds(80) };

        // Act & Assert
        var error = await Should.ThrowAsync<TimeoutException>(() => actions.MoveToRecycleBinAsync([Report], TestContext.Current.CancellationToken));
        error.Message.ShouldContain("«Операции»");
    }

    [Fact]
    public async Task Clipboard_PathsAndFiles_GoToShellClipboard()
    {
        // Arrange
        var (api, _) = FakeDiskAppApi.Create();
        var actions = new ApiFileActions(api, _clipboard);
        var other = Report with { FullPath = @"D:\Docs\Бюджет.xlsx" };

        // Act
        await actions.CopyPathsAsync([Report, other], TestContext.Current.CancellationToken);
        await actions.CopyFilesAsync([Report], TestContext.Current.CancellationToken);

        // Assert
        _clipboard.Text.ShouldBe(Report.FullPath + Environment.NewLine + other.FullPath);
        _clipboard.Files.ShouldBe([Report.FullPath]);
    }

    [Fact]
    public async Task SetFavorite_AddAndRemove_CallsFavoritesApi()
    {
        // Arrange
        var (api, fake) = FakeDiskAppApi.Create();
        fake.Handlers[nameof(IDiskAppApi.AddFavoriteAsync)] = args => Task.FromResult(new EntryDto { Id = (long)args[0]!, IsFavorite = true });
        fake.Handlers[nameof(IDiskAppApi.RemoveFavoriteAsync)] = _ => Task.CompletedTask;
        var actions = new ApiFileActions(api, _clipboard);

        // Act
        var pinned = await actions.SetFavoriteAsync(Report, favorite: true, TestContext.Current.CancellationToken);
        var unpinned = await actions.SetFavoriteAsync(pinned, favorite: false, TestContext.Current.CancellationToken);

        // Assert
        pinned.IsFavorite.ShouldBeTrue();
        unpinned.IsFavorite.ShouldBeFalse();
        fake.Calls.ShouldBe([nameof(IDiskAppApi.AddFavoriteAsync), nameof(IDiskAppApi.RemoveFavoriteAsync)]);
    }

    [Fact]
    public async Task ShellActions_PassEntryIdToServer()
    {
        // Arrange
        var (api, fake) = FakeDiskAppApi.Create();
        var ids = new List<long>();
        foreach (var method in new[] { nameof(IDiskAppApi.RevealAsync), nameof(IDiskAppApi.OpenWithAsync), nameof(IDiskAppApi.ShowPropertiesAsync), nameof(IDiskAppApi.RunAsAdminAsync) })
        {
            fake.Handlers[method] = args =>
            {
                ids.Add((long)args[0]!);
                return Task.CompletedTask;
            };
        }

        var actions = new ApiFileActions(api, _clipboard);
        var token = TestContext.Current.CancellationToken;

        // Act
        await actions.RevealAsync(Report, token);
        await actions.OpenWithAsync(Report, token);
        await actions.ShowPropertiesAsync(Report, token);
        await actions.RunAsAdminAsync(Report, token);

        // Assert
        ids.ShouldBe([7L, 7L, 7L, 7L]);
    }

    private static OperationDto Operation(OperationState state)
        => new(Guid.Parse("11111111-2222-3333-4444-555555555555"), OperationKind.Rename, state, [Report.FullPath], null,
            DateTimeOffset.UnixEpoch, null, new OperationProgressDto(Guid.Empty, 0, 0, 0, 0, null, 0, null), null, CanUndo: false, PendingConflict: null);

    private sealed class FakeClipboard : IClipboardService
    {
        public string? Text { get; private set; }

        public IReadOnlyList<string>? Files { get; private set; }

        public Task SetTextAsync(string text, CancellationToken cancellationToken = default)
        {
            Text = text;
            return Task.CompletedTask;
        }

        public Task SetFilesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
        {
            Files = paths;
            return Task.CompletedTask;
        }
    }
}
